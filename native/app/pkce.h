#pragma once
#include <string>
#include <vector>

namespace mn {

// PKCE (RFC 7636) helpers plus the small primitives auth needs, mirroring
// lookxy's mailcore pkce.rs — except SHA-256 and randomness come from
// bcrypt (system-preferred RNG / CNG SHA-256) instead of being hand-rolled.

struct Pkce {
    std::string verifier;   // 32 random bytes, base64url
    std::string challenge;  // base64url(SHA256(verifier))
    static Pkce generate();
};

std::vector<unsigned char> randomBytes(size_t n);
bool sha256(const void* data, size_t size, unsigned char out[32]);

// Base64url (RFC 4648 section 5), no padding.
std::string base64urlEncode(const unsigned char* bytes, size_t n);
std::vector<unsigned char> base64urlDecode(const std::string& s);  // empty on invalid input

// application/x-www-form-urlencoded: unreserved chars pass, rest %XX.
std::string percentEncode(const std::string& s);
std::string formUrlEncode(const std::vector<std::pair<std::string, std::string>>& pairs);
std::string urlDecode(const std::string& s);  // %XX + '+' → byte

} // namespace mn
