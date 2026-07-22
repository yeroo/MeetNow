#include "pkce.h"
#include <windows.h>
#include <bcrypt.h>
#include <cstdio>

namespace mn {

std::vector<unsigned char> randomBytes(size_t n) {
    std::vector<unsigned char> buf(n);
    if (BCryptGenRandom(nullptr, buf.data(), (ULONG)n, BCRYPT_USE_SYSTEM_PREFERRED_RNG) != 0)
        buf.clear();
    return buf;
}

bool sha256(const void* data, size_t size, unsigned char out[32]) {
    BCRYPT_ALG_HANDLE alg = nullptr;
    if (BCryptOpenAlgorithmProvider(&alg, BCRYPT_SHA256_ALGORITHM, nullptr, 0) != 0) return false;
    const NTSTATUS status = BCryptHash(alg, nullptr, 0, (PUCHAR)data, (ULONG)size, out, 32);
    BCryptCloseAlgorithmProvider(alg, 0);
    return status == 0;
}

static const char kAlphabet[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

std::string base64urlEncode(const unsigned char* bytes, size_t n) {
    std::string out;
    out.reserve((n + 2) / 3 * 4);
    for (size_t i = 0; i < n; i += 3) {
        const unsigned b0 = bytes[i];
        const unsigned b1 = i + 1 < n ? bytes[i + 1] : 0;
        const unsigned b2 = i + 2 < n ? bytes[i + 2] : 0;
        const unsigned v = (b0 << 16) | (b1 << 8) | b2;
        out += kAlphabet[(v >> 18) & 0x3f];
        out += kAlphabet[(v >> 12) & 0x3f];
        if (i + 1 < n) out += kAlphabet[(v >> 6) & 0x3f];
        if (i + 2 < n) out += kAlphabet[v & 0x3f];
    }
    return out;
}

static int b64Digit(char c) {
    if (c >= 'A' && c <= 'Z') return c - 'A';
    if (c >= 'a' && c <= 'z') return c - 'a' + 26;
    if (c >= '0' && c <= '9') return c - '0' + 52;
    if (c == '-') return 62;
    if (c == '_') return 63;
    return -1;
}

std::vector<unsigned char> base64urlDecode(const std::string& s) {
    if (s.size() % 4 == 1) return {};
    std::vector<unsigned char> out;
    out.reserve(s.size() * 3 / 4);
    for (size_t i = 0; i < s.size(); i += 4) {
        unsigned v = 0;
        const size_t len = s.size() - i < 4 ? s.size() - i : 4;
        for (size_t j = 0; j < 4; ++j) {
            const int d = j < len ? b64Digit(s[i + j]) : 0;
            if (d < 0) return {};
            v = (v << 6) | (unsigned)d;
        }
        out.push_back((unsigned char)(v >> 16));
        if (len > 2) out.push_back((unsigned char)(v >> 8));
        if (len > 3) out.push_back((unsigned char)v);
    }
    return out;
}

Pkce Pkce::generate() {
    Pkce p;
    const auto bytes = randomBytes(32);
    if (bytes.empty()) return p;
    p.verifier = base64urlEncode(bytes.data(), bytes.size());
    unsigned char digest[32];
    if (sha256(p.verifier.data(), p.verifier.size(), digest))
        p.challenge = base64urlEncode(digest, 32);
    return p;
}

std::string percentEncode(const std::string& s) {
    std::string out;
    out.reserve(s.size());
    for (const char c : s) {
        const bool unreserved = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                                (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.' || c == '~';
        if (unreserved) {
            out += c;
        } else {
            char buf[4];
            snprintf(buf, sizeof(buf), "%%%02X", (unsigned char)c);
            out += buf;
        }
    }
    return out;
}

std::string formUrlEncode(const std::vector<std::pair<std::string, std::string>>& pairs) {
    std::string out;
    for (const auto& [k, v] : pairs) {
        if (!out.empty()) out += '&';
        out += percentEncode(k);
        out += '=';
        out += percentEncode(v);
    }
    return out;
}

std::string urlDecode(const std::string& s) {
    std::string out;
    out.reserve(s.size());
    for (size_t i = 0; i < s.size(); ++i) {
        if (s[i] == '+') {
            out += ' ';
        } else if (s[i] == '%' && i + 2 < s.size()) {
            const auto hex = [](char c) -> int {
                if (c >= '0' && c <= '9') return c - '0';
                if (c >= 'a' && c <= 'f') return c - 'a' + 10;
                if (c >= 'A' && c <= 'F') return c - 'A' + 10;
                return -1;
            };
            const int hi = hex(s[i + 1]), lo = hex(s[i + 2]);
            if (hi >= 0 && lo >= 0) {
                out += (char)((hi << 4) | lo);
                i += 2;
            } else {
                out += s[i];
            }
        } else {
            out += s[i];
        }
    }
    return out;
}

} // namespace mn
