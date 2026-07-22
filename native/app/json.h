#pragma once
#include <memory>
#include <string>
#include <vector>

namespace mn::json {

// Minimal JSON model, ported from the BrowserSelect native rewrite. Objects
// preserve insertion order; parsing is depth-limited and returns nullptr on
// malformed input (no exceptions anywhere in this app).
struct Value;
using ValuePtr = std::shared_ptr<Value>;

enum class Type { Null, Bool, Number, String, Array, Object };

struct Value {
    Type type = Type::Null;
    bool boolean = false;
    double number = 0.0;
    std::wstring str;
    std::vector<ValuePtr> array;
    std::vector<std::pair<std::wstring, ValuePtr>> object;

    static ValuePtr makeObject() { auto v = std::make_shared<Value>(); v->type = Type::Object; return v; }
    static ValuePtr makeArray()  { auto v = std::make_shared<Value>(); v->type = Type::Array;  return v; }
    static ValuePtr makeString(const std::wstring& s) {
        auto v = std::make_shared<Value>(); v->type = Type::String; v->str = s; return v;
    }
    static ValuePtr makeNumber(double n) {
        auto v = std::make_shared<Value>(); v->type = Type::Number; v->number = n; return v;
    }

    // Object lookup; nullptr when missing or not an object.
    ValuePtr get(const std::wstring& key) const {
        if (type != Type::Object) return nullptr;
        for (const auto& [k, v] : object)
            if (k == key) return v;
        return nullptr;
    }
    void set(const std::wstring& key, ValuePtr val) {
        for (auto& [k, v] : object)
            if (k == key) { v = std::move(val); return; }
        object.emplace_back(key, std::move(val));
    }
    // Convenience typed getters; defaults when missing or wrong type.
    std::wstring getString(const std::wstring& key) const {
        const auto v = get(key);
        return v && v->type == Type::String ? v->str : L"";
    }
    double getNumber(const std::wstring& key, double fallback = 0.0) const {
        const auto v = get(key);
        return v && v->type == Type::Number ? v->number : fallback;
    }
    bool getBool(const std::wstring& key, bool fallback = false) const {
        const auto v = get(key);
        return v && v->type == Type::Bool ? v->boolean : fallback;
    }
};

// Parse UTF-8 JSON text. Returns nullptr on malformed input.
ValuePtr parse(const std::string& utf8);

// Serialize with 2-space indent and \r\n line ends.
std::string serializeIndented(const ValuePtr& v);

} // namespace mn::json
