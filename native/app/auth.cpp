#include "auth.h"
#include "http.h"
#include "json.h"
#include "pkce.h"
#include "str.h"
#include <windows.h>
#include <winsock2.h>
#include <ws2tcpip.h>
#include <dpapi.h>
#include <shellapi.h>
#include <ctime>

namespace mn {

namespace {

std::wstring tokenCachePath() {
    return settingsDir() + L"\\token.bin";
}

// The tenant segment of the /oauth2/v2.0 endpoints: last path segment of
// the authority, tolerating a trailing slash (mirrors auth.rs tenant_of).
std::string tenantOf(const std::wstring& authority) {
    std::string a = narrow(authority);
    while (!a.empty() && a.back() == '/') a.pop_back();
    const size_t p = a.find_last_of('/');
    return p == std::string::npos ? a : a.substr(p + 1);
}

unsigned long long unixNow() {
    return (unsigned long long)time(nullptr);
}

// Extracts preferred_username from a JWT payload — display only, the token
// came straight from Entra ID over TLS (mirrors auth.rs preferred_username).
std::wstring preferredUsername(const std::string& idToken) {
    const size_t dot1 = idToken.find('.');
    if (dot1 == std::string::npos) return L"";
    const size_t dot2 = idToken.find('.', dot1 + 1);
    if (dot2 == std::string::npos) return L"";
    const auto payload = base64urlDecode(idToken.substr(dot1 + 1, dot2 - dot1 - 1));
    if (payload.empty()) return L"";
    const auto v = json::parse(std::string(payload.begin(), payload.end()));
    if (!v) return L"";
    return v->getString(L"preferred_username");
}

// POST to the token endpoint and parse the response (auth.rs post_token +
// parse_token_response). Entra ID may omit refresh_token on a refresh
// response; the caller's previous refresh token is carried forward then.
TokenSet postToken(const Settings& s, const std::string& body,
                   const std::string& fallbackRefreshToken, std::wstring* err) {
    TokenSet t;
    const std::wstring url = L"https://login.microsoftonline.com/" +
                             widen(tenantOf(s.authority)) + L"/oauth2/v2.0/token";
    const HttpResponse resp = httpPost(url, L"application/x-www-form-urlencoded", body);
    const auto v = json::parse(resp.body);
    if (resp.status < 200 || resp.status >= 300 || !v) {
        if (err) {
            // Entra ID error bodies carry a diagnostic error_description
            // (never a token or the code verifier).
            std::wstring desc = v ? v->getString(L"error_description") : L"";
            if (desc.empty() && v) desc = v->getString(L"error");
            *err = desc.empty() ? L"token endpoint HTTP " + std::to_wstring(resp.status) : desc;
        }
        return t;
    }
    t.accessToken = narrow(v->getString(L"access_token"));
    t.refreshToken = narrow(v->getString(L"refresh_token"));
    if (t.refreshToken.empty()) t.refreshToken = fallbackRefreshToken;
    const double expiresIn = v->getNumber(L"expires_in");
    t.expiresAtUnix = unixNow() + (unsigned long long)(expiresIn > 0 ? expiresIn : 0);
    t.account = preferredUsername(narrow(v->getString(L"id_token")));
    if (t.accessToken.empty() && err) *err = L"response has no access_token";
    return t;
}

// --- Loopback redirect listener ------------------------------------------

// Accepts connections on 127.0.0.1:<port> until a request carrying ?code=
// (or ?error=) with our state arrives; anything else (favicon probes) gets
// a 404 and the wait continues. Returns false on timeout/wrong state.
bool waitForRedirect(SOCKET listener, const std::string& state, DWORD timeoutMs,
                     std::string* code, std::wstring* err) {
    const ULONGLONG deadline = GetTickCount64() + timeoutMs;
    for (;;) {
        const ULONGLONG now = GetTickCount64();
        if (now >= deadline) {
            if (err) *err = L"timed out waiting for the browser sign-in";
            return false;
        }
        timeval tv{};
        tv.tv_sec = (long)((deadline - now) / 1000);
        tv.tv_usec = (long)(((deadline - now) % 1000) * 1000);
        fd_set fds;
        FD_ZERO(&fds);
        FD_SET(listener, &fds);
        if (select(0, &fds, nullptr, nullptr, &tv) <= 0) {
            if (err) *err = L"timed out waiting for the browser sign-in";
            return false;
        }
        SOCKET client = accept(listener, nullptr, nullptr);
        if (client == INVALID_SOCKET) continue;

        std::string request;
        char buf[4096];
        for (;;) {
            const int got = recv(client, buf, sizeof(buf), 0);
            if (got <= 0) break;
            request.append(buf, (size_t)got);
            if (request.find("\r\n\r\n") != std::string::npos || request.size() > 64 * 1024) break;
        }

        // "GET /?code=...&state=... HTTP/1.1"
        std::string query;
        const size_t qStart = request.find("GET /");
        if (qStart == 0) {
            const size_t sp = request.find(' ', 4);
            if (sp != std::string::npos) {
                const std::string target = request.substr(4, sp - 4);
                const size_t q = target.find('?');
                if (q != std::string::npos) query = target.substr(q + 1);
            }
        }

        std::string gotCode, gotState, gotError;
        size_t pos = 0;
        while (pos < query.size()) {
            size_t amp = query.find('&', pos);
            if (amp == std::string::npos) amp = query.size();
            const std::string pair = query.substr(pos, amp - pos);
            const size_t eq = pair.find('=');
            if (eq != std::string::npos) {
                const std::string key = pair.substr(0, eq);
                const std::string val = urlDecode(pair.substr(eq + 1));
                if (key == "code") gotCode = val;
                else if (key == "state") gotState = val;
                else if (key == "error_description") gotError = val;
                else if (key == "error" && gotError.empty()) gotError = val;
            }
            pos = amp + 1;
        }

        const bool relevant = !gotCode.empty() || !gotError.empty();
        const char* html =
            relevant && gotError.empty()
                ? "<html><body style=\"font-family:sans-serif\">"
                  "<h3>MeetNow is signed in.</h3>You can close this tab.</body></html>"
                : "<html><body style=\"font-family:sans-serif\">"
                  "<h3>Sign-in failed.</h3>You can close this tab.</body></html>";
        char response[1024];
        _snprintf_s(response, _TRUNCATE,
                    "HTTP/1.1 %s\r\nContent-Type: text/html\r\nContent-Length: %zu\r\n"
                    "Connection: close\r\n\r\n%s",
                    relevant ? "200 OK" : "404 Not Found", strlen(html), html);
        send(client, response, (int)strlen(response), 0);
        shutdown(client, SD_SEND);
        closesocket(client);

        if (!relevant) continue;  // favicon or stray request; keep waiting
        if (!gotError.empty()) {
            if (err) *err = widen(gotError);
            return false;
        }
        if (gotState != state) {
            if (err) *err = L"state mismatch on the sign-in redirect";
            return false;
        }
        *code = gotCode;
        return true;
    }
}

} // namespace

// --- Token cache (tokencache.rs: DPAPI-at-rest, atomic write) -------------

TokenSet loadTokenCache() {
    TokenSet t;
    const std::string cipher = readFileBytes(tokenCachePath());
    if (cipher.empty()) return t;

    DATA_BLOB in{ (DWORD)cipher.size(), (BYTE*)cipher.data() };
    DATA_BLOB out{};
    if (!CryptUnprotectData(&in, nullptr, nullptr, nullptr, nullptr,
                            CRYPTPROTECT_UI_FORBIDDEN, &out))
        return t;
    const std::string plain((const char*)out.pbData, out.cbData);
    LocalFree(out.pbData);

    const auto v = json::parse(plain);
    if (!v) return t;
    t.accessToken = narrow(v->getString(L"access_token"));
    t.refreshToken = narrow(v->getString(L"refresh_token"));
    t.expiresAtUnix = (unsigned long long)v->getNumber(L"expires_at_unix");
    t.account = v->getString(L"account");
    return t;
}

bool saveTokenCache(const TokenSet& t) {
    auto v = json::Value::makeObject();
    v->set(L"access_token", json::Value::makeString(widen(t.accessToken)));
    v->set(L"refresh_token", json::Value::makeString(widen(t.refreshToken)));
    v->set(L"expires_at_unix", json::Value::makeNumber((double)t.expiresAtUnix));
    v->set(L"account", json::Value::makeString(t.account));
    const std::string plain = json::serializeIndented(v);

    DATA_BLOB in{ (DWORD)plain.size(), (BYTE*)plain.data() };
    DATA_BLOB out{};
    if (!CryptProtectData(&in, L"MeetNow token cache", nullptr, nullptr, nullptr,
                          CRYPTPROTECT_UI_FORBIDDEN, &out))
        return false;
    const bool ok = writeFileAtomic(tokenCachePath(), out.pbData, out.cbData);
    LocalFree(out.pbData);
    return ok;
}

// --- Flows ----------------------------------------------------------------

TokenSet ensureFresh(const Settings& s, const TokenSet& t) {
    if (t.empty()) return {};
    if (t.expiresAtUnix > unixNow() + 60) return t;
    if (t.refreshToken.empty()) return {};
    const std::string body = formUrlEncode({
        { "grant_type", "refresh_token" },
        { "client_id", narrow(s.clientId) },
        { "refresh_token", t.refreshToken },
        { "scope", narrow(s.scope) },
    });
    TokenSet fresh = postToken(s, body, t.refreshToken, nullptr);
    if (fresh.empty()) return {};
    if (fresh.account.empty()) fresh.account = t.account;
    saveTokenCache(fresh);
    return fresh;
}

TokenSet acquireTokenSilent(const Settings& s) {
    return ensureFresh(s, loadTokenCache());
}

TokenSet acquireTokenInteractive(const Settings& s, std::wstring* err) {
    TokenSet t;

    // Loopback listener on an OS-assigned port; Entra ID native clients may
    // use any port with an http://localhost redirect.
    SOCKET listener = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (listener == INVALID_SOCKET) {
        if (err) *err = L"could not create the loopback socket";
        return t;
    }
    sockaddr_in addr{};
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (bind(listener, (sockaddr*)&addr, sizeof(addr)) != 0 || listen(listener, 1) != 0) {
        closesocket(listener);
        if (err) *err = L"could not listen on the loopback socket";
        return t;
    }
    int addrLen = sizeof(addr);
    getsockname(listener, (sockaddr*)&addr, &addrLen);
    const std::string redirectUri = "http://localhost:" + std::to_string(ntohs(addr.sin_port));

    // begin_auth (auth.rs): PKCE pair + OS-randomness state, then the
    // /authorize URL the system browser drives.
    const Pkce pkce = Pkce::generate();
    const std::string state = Pkce::generate().verifier;
    if (pkce.challenge.empty() || state.empty()) {
        closesocket(listener);
        if (err) *err = L"could not generate the PKCE challenge";
        return t;
    }
    const std::string query = formUrlEncode({
        { "client_id", narrow(s.clientId) },
        { "response_type", "code" },
        { "redirect_uri", redirectUri },
        { "response_mode", "query" },
        { "scope", narrow(s.scope) },
        { "state", state },
        { "code_challenge", pkce.challenge },
        { "code_challenge_method", "S256" },
        { "prompt", "select_account" },
    });
    const std::wstring authorizeUrl = L"https://login.microsoftonline.com/" +
                                      widen(tenantOf(s.authority)) +
                                      L"/oauth2/v2.0/authorize?" + widen(query);
    ShellExecuteW(nullptr, L"open", authorizeUrl.c_str(), nullptr, nullptr, SW_SHOWNORMAL);

    std::string code;
    const bool got = waitForRedirect(listener, state, 3 * 60 * 1000, &code, err);
    closesocket(listener);
    if (!got) return t;

    // redeem_code (auth.rs).
    const std::string body = formUrlEncode({
        { "grant_type", "authorization_code" },
        { "client_id", narrow(s.clientId) },
        { "code", code },
        { "redirect_uri", redirectUri },
        { "code_verifier", pkce.verifier },
        { "scope", narrow(s.scope) },
    });
    t = postToken(s, body, "", err);
    if (!t.empty()) saveTokenCache(t);
    return t;
}

} // namespace mn
