#pragma once
#include <string>
#include <vector>

namespace mn {

// Blocking HTTPS via WinHTTP — replaces lookxy's ureq. One request per
// call, no connection reuse; the app talks to the network at most every
// 15 minutes, so simplicity wins over keep-alive.

struct HttpResponse {
    int status = 0;        // 0 = transport failure (no response at all)
    std::string body;      // raw bytes (UTF-8 for both endpoints we call)
};

// `headers` are extra request headers, "Name: value" per entry.
HttpResponse httpGet(const std::wstring& url, const std::vector<std::wstring>& headers);
HttpResponse httpPost(const std::wstring& url, const std::wstring& contentType,
                      const std::string& body);

} // namespace mn
