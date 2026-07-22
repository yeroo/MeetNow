#include "json.h"
#include "str.h"
#include <cmath>
#include <cstdio>

namespace mn::json {

namespace {

struct Parser {
    const char* p;
    const char* end;
    int depth = 0;

    void skipWs() {
        while (p < end && (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n')) ++p;
    }
    bool eat(char c) {
        skipWs();
        if (p < end && *p == c) { ++p; return true; }
        return false;
    }

    ValuePtr parseValue() {
        if (++depth > 64) return nullptr;
        skipWs();
        if (p >= end) return nullptr;
        ValuePtr result;
        switch (*p) {
        case '{': result = parseObject(); break;
        case '[': result = parseArray(); break;
        case '"': result = parseString(); break;
        case 't':
            if (end - p >= 4 && memcmp(p, "true", 4) == 0) {
                p += 4; result = std::make_shared<Value>(); result->type = Type::Bool; result->boolean = true;
            }
            break;
        case 'f':
            if (end - p >= 5 && memcmp(p, "false", 5) == 0) {
                p += 5; result = std::make_shared<Value>(); result->type = Type::Bool; result->boolean = false;
            }
            break;
        case 'n':
            if (end - p >= 4 && memcmp(p, "null", 4) == 0) {
                p += 4; result = std::make_shared<Value>();
            }
            break;
        default:  result = parseNumber(); break;
        }
        --depth;
        return result;
    }

    ValuePtr parseObject() {
        ++p; // '{'
        auto v = Value::makeObject();
        skipWs();
        if (eat('}')) return v;
        for (;;) {
            skipWs();
            if (p >= end || *p != '"') return nullptr;
            auto key = parseString();
            if (!key) return nullptr;
            if (!eat(':')) return nullptr;
            auto val = parseValue();
            if (!val) return nullptr;
            v->object.emplace_back(key->str, val);
            if (eat(',')) continue;
            if (eat('}')) return v;
            return nullptr;
        }
    }

    ValuePtr parseArray() {
        ++p; // '['
        auto v = Value::makeArray();
        skipWs();
        if (eat(']')) return v;
        for (;;) {
            auto val = parseValue();
            if (!val) return nullptr;
            v->array.push_back(val);
            if (eat(',')) continue;
            if (eat(']')) return v;
            return nullptr;
        }
    }

    ValuePtr parseString() {
        ++p; // '"'
        std::string out;
        while (p < end && *p != '"') {
            if (*p == '\\') {
                ++p;
                if (p >= end) return nullptr;
                switch (*p) {
                case '"': out += '"'; break;
                case '\\': out += '\\'; break;
                case '/': out += '/'; break;
                case 'b': out += '\b'; break;
                case 'f': out += '\f'; break;
                case 'n': out += '\n'; break;
                case 'r': out += '\r'; break;
                case 't': out += '\t'; break;
                case 'u': {
                    if (end - p < 5) return nullptr;
                    unsigned cp = 0;
                    for (int i = 1; i <= 4; ++i) {
                        const char c = p[i];
                        cp <<= 4;
                        if (c >= '0' && c <= '9') cp |= (unsigned)(c - '0');
                        else if (c >= 'a' && c <= 'f') cp |= (unsigned)(c - 'a' + 10);
                        else if (c >= 'A' && c <= 'F') cp |= (unsigned)(c - 'A' + 10);
                        else return nullptr;
                    }
                    p += 4;
                    // surrogate pair
                    if (cp >= 0xD800 && cp <= 0xDBFF && end - p >= 7 && p[1] == '\\' && p[2] == 'u') {
                        unsigned lo = 0;
                        bool ok = true;
                        for (int i = 3; i <= 6; ++i) {
                            const char c = p[i];
                            lo <<= 4;
                            if (c >= '0' && c <= '9') lo |= (unsigned)(c - '0');
                            else if (c >= 'a' && c <= 'f') lo |= (unsigned)(c - 'a' + 10);
                            else if (c >= 'A' && c <= 'F') lo |= (unsigned)(c - 'A' + 10);
                            else { ok = false; break; }
                        }
                        if (ok && lo >= 0xDC00 && lo <= 0xDFFF) {
                            cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00);
                            p += 6;
                        }
                    }
                    // encode cp as UTF-8
                    if (cp < 0x80) out += (char)cp;
                    else if (cp < 0x800) {
                        out += (char)(0xC0 | (cp >> 6));
                        out += (char)(0x80 | (cp & 0x3F));
                    } else if (cp < 0x10000) {
                        out += (char)(0xE0 | (cp >> 12));
                        out += (char)(0x80 | ((cp >> 6) & 0x3F));
                        out += (char)(0x80 | (cp & 0x3F));
                    } else {
                        out += (char)(0xF0 | (cp >> 18));
                        out += (char)(0x80 | ((cp >> 12) & 0x3F));
                        out += (char)(0x80 | ((cp >> 6) & 0x3F));
                        out += (char)(0x80 | (cp & 0x3F));
                    }
                    break;
                }
                default: return nullptr;
                }
                ++p;
            } else {
                out += *p++;
            }
        }
        if (p >= end) return nullptr;
        ++p; // closing '"'
        return Value::makeString(widen(out));
    }

    ValuePtr parseNumber() {
        const char* start = p;
        if (p < end && (*p == '-' || *p == '+')) ++p;
        while (p < end && ((*p >= '0' && *p <= '9') || *p == '.' || *p == 'e' || *p == 'E' || *p == '-' || *p == '+')) ++p;
        if (p == start) return nullptr;
        auto v = std::make_shared<Value>();
        v->type = Type::Number;
        v->number = strtod(std::string(start, p).c_str(), nullptr);
        return v;
    }
};

void appendEscaped(std::string& out, const std::wstring& s) {
    out += '"';
    const std::string utf8 = narrow(s);
    for (const char c : utf8) {
        switch (c) {
        case '"': out += "\\\""; break;
        case '\\': out += "\\\\"; break;
        case '\b': out += "\\b"; break;
        case '\f': out += "\\f"; break;
        case '\n': out += "\\n"; break;
        case '\r': out += "\\r"; break;
        case '\t': out += "\\t"; break;
        default:
            if ((unsigned char)c < 0x20) {
                char buf[8];
                snprintf(buf, sizeof(buf), "\\u%04x", (unsigned char)c);
                out += buf;
            } else {
                out += c;
            }
        }
    }
    out += '"';
}

void serialize(std::string& out, const ValuePtr& v, int indent) {
    const std::string pad((size_t)indent * 2, ' ');
    const std::string padIn((size_t)(indent + 1) * 2, ' ');
    if (!v) { out += "null"; return; }
    switch (v->type) {
    case Type::Null:   out += "null"; break;
    case Type::Bool:   out += v->boolean ? "true" : "false"; break;
    case Type::Number: {
        char buf[32];
        if (v->number == std::floor(v->number) && std::abs(v->number) < 1e15)
            snprintf(buf, sizeof(buf), "%.0f", v->number);
        else
            snprintf(buf, sizeof(buf), "%g", v->number);
        out += buf;
        break;
    }
    case Type::String: appendEscaped(out, v->str); break;
    case Type::Array:
        if (v->array.empty()) { out += "[]"; break; }
        out += "[\r\n";
        for (size_t i = 0; i < v->array.size(); ++i) {
            out += padIn;
            serialize(out, v->array[i], indent + 1);
            if (i + 1 < v->array.size()) out += ',';
            out += "\r\n";
        }
        out += pad;
        out += ']';
        break;
    case Type::Object:
        if (v->object.empty()) { out += "{}"; break; }
        out += "{\r\n";
        for (size_t i = 0; i < v->object.size(); ++i) {
            out += padIn;
            appendEscaped(out, v->object[i].first);
            out += ": ";
            serialize(out, v->object[i].second, indent + 1);
            if (i + 1 < v->object.size()) out += ',';
            out += "\r\n";
        }
        out += pad;
        out += '}';
        break;
    }
}

} // namespace

ValuePtr parse(const std::string& utf8) {
    const char* start = utf8.data();
    const char* end = start + utf8.size();
    if (utf8.size() >= 3 && (unsigned char)utf8[0] == 0xEF && (unsigned char)utf8[1] == 0xBB && (unsigned char)utf8[2] == 0xBF)
        start += 3; // skip UTF-8 BOM
    Parser parser{ start, end };
    auto v = parser.parseValue();
    if (!v) return nullptr;
    parser.skipWs();
    if (parser.p != parser.end) return nullptr;
    return v;
}

std::string serializeIndented(const ValuePtr& v) {
    std::string out;
    serialize(out, v, 0);
    return out;
}

} // namespace mn::json
