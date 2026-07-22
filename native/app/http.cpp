#include "http.h"
#include <windows.h>
#include <winhttp.h>

namespace mn {

namespace {

HttpResponse request(const wchar_t* verb, const std::wstring& url,
                     const std::vector<std::wstring>& headers,
                     const std::wstring& contentType, const std::string& body) {
    HttpResponse resp;

    URL_COMPONENTS parts{};
    parts.dwStructSize = sizeof(parts);
    wchar_t host[256]{}, path[4096]{};
    parts.lpszHostName = host;
    parts.dwHostNameLength = ARRAYSIZE(host);
    parts.lpszUrlPath = path;
    parts.dwUrlPathLength = ARRAYSIZE(path);
    if (!WinHttpCrackUrl(url.c_str(), (DWORD)url.size(), 0, &parts)) return resp;

    // WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY honors the system/PAC proxy the
    // way the .NET HttpClient in the C# app did.
    HINTERNET session = WinHttpOpen(L"MeetNow/1.0", WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY,
                                    WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (!session) return resp;

    HINTERNET connect = nullptr, req = nullptr;
    do {
        connect = WinHttpConnect(session, host, parts.nPort, 0);
        if (!connect) break;
        const DWORD flags = parts.nScheme == INTERNET_SCHEME_HTTPS ? WINHTTP_FLAG_SECURE : 0;
        req = WinHttpOpenRequest(connect, verb, path, nullptr, WINHTTP_NO_REFERER,
                                 WINHTTP_DEFAULT_ACCEPT_TYPES, flags);
        if (!req) break;

        for (const auto& h : headers)
            WinHttpAddRequestHeaders(req, h.c_str(), (DWORD)h.size(), WINHTTP_ADDREQ_FLAG_ADD);
        if (!contentType.empty()) {
            const std::wstring h = L"Content-Type: " + contentType;
            WinHttpAddRequestHeaders(req, h.c_str(), (DWORD)h.size(), WINHTTP_ADDREQ_FLAG_ADD);
        }

        if (!WinHttpSendRequest(req, WINHTTP_NO_ADDITIONAL_HEADERS, 0,
                                body.empty() ? WINHTTP_NO_REQUEST_DATA : (LPVOID)body.data(),
                                (DWORD)body.size(), (DWORD)body.size(), 0))
            break;
        if (!WinHttpReceiveResponse(req, nullptr)) break;

        DWORD status = 0, statusSize = sizeof(status);
        if (!WinHttpQueryHeaders(req, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                                 WINHTTP_HEADER_NAME_BY_INDEX, &status, &statusSize,
                                 WINHTTP_NO_HEADER_INDEX))
            break;
        resp.status = (int)status;

        for (;;) {
            DWORD avail = 0;
            if (!WinHttpQueryDataAvailable(req, &avail) || avail == 0) break;
            if (resp.body.size() + avail > 64 * 1024 * 1024) break;  // sanity cap
            const size_t off = resp.body.size();
            resp.body.resize(off + avail);
            DWORD got = 0;
            if (!WinHttpReadData(req, resp.body.data() + off, avail, &got)) {
                resp.body.resize(off);
                break;
            }
            resp.body.resize(off + got);
            if (got == 0) break;
        }
    } while (false);

    if (req) WinHttpCloseHandle(req);
    if (connect) WinHttpCloseHandle(connect);
    WinHttpCloseHandle(session);
    return resp;
}

} // namespace

HttpResponse httpGet(const std::wstring& url, const std::vector<std::wstring>& headers) {
    return request(L"GET", url, headers, L"", {});
}

HttpResponse httpPost(const std::wstring& url, const std::wstring& contentType,
                      const std::string& body) {
    return request(L"POST", url, {}, contentType, body);
}

} // namespace mn
