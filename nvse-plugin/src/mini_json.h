#pragma once
// Tiny JSON helpers for the flat messages of the MDR protocol (docs/PROTOCOL.md).
// Parsing accepts any valid JSON object; nested objects/arrays are skipped.

#include <string>
#include <unordered_map>

namespace mdr::json
{
    struct Value
    {
        enum class Type { Null, Bool, Number, String } type = Type::Null;
        bool b = false;
        double n = 0;
        std::string s; // UTF-8
    };

    using Object = std::unordered_map<std::string, Value>;

    bool ParseObject(const std::string& text, Object& out);

    std::string Escape(const std::string& utf8);

    // Accessors with defaults
    std::string GetString(const Object& o, const char* key, const char* def = "");
    double GetNumber(const Object& o, const char* key, double def = 0);
    bool GetBool(const Object& o, const char* key, bool def = false);

    // Builder for one flat object: Writer w("cmd"); w.Str("action","next"); w.Num("id",3); w.Finish();
    class Writer
    {
    public:
        explicit Writer(const char* type);
        Writer& Str(const char* key, const std::string& value);
        Writer& Num(const char* key, double value);
        Writer& Int(const char* key, long long value);
        Writer& Bool(const char* key, bool value);
        std::string Finish();

    private:
        std::string m_out;
    };
}
