#pragma once
#include "settings.h"
#include <string>

namespace mn {

// OAuth2 authorization-code + PKCE against Microsoft Entra ID, ported from
// lookxy's mailcore auth.rs/tokencache.rs: interactive sign-in happens in
// the system browser with an http://localhost:<port> loopback redirect
// (device-code flow is blocked by Conditional Access in the target tenant).
// Tokens are cached DPAPI-encrypted in %LOCALAPPDATA%\MeetNow\token.bin.
// Token values are never logged.

struct TokenSet {
    std::string accessToken;
    std::string refreshToken;
    unsigned long long expiresAtUnix = 0;
    std::wstring account;  // preferred_username claim, display only

    bool empty() const { return accessToken.empty(); }
};

// Loads the DPAPI-encrypted token cache; empty TokenSet when absent/invalid.
TokenSet loadTokenCache();
bool saveTokenCache(const TokenSet& t);

// Silent path: cached token if still valid, else refresh-token exchange.
// Never opens a browser. Empty TokenSet when sign-in is required.
TokenSet acquireTokenSilent(const Settings& s);

// Interactive path: loopback listener + system browser + code redemption.
// Blocks for up to ~3 minutes waiting for the redirect; call from a worker
// thread. `err` (optional) receives a short diagnostic on failure.
TokenSet acquireTokenInteractive(const Settings& s, std::wstring* err);

// Refresh `t` if it expires within 60 s. Returns the (possibly unchanged)
// token set; empty when the refresh was needed and failed.
TokenSet ensureFresh(const Settings& s, const TokenSet& t);

} // namespace mn
