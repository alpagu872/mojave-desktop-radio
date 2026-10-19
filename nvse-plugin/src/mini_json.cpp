#include "mini_json.h"

#include <algorithm>
#include <cstdio>
#include <cstdlib>

namespace mdr::json
{
    namespace
    {
        struct Parser
        {
            const char* p;
            const char* end;

            void SkipWs()
            {
                while (p < end && (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n')) ++p;
            }

            static void AppendUtf8(std::string& out, unsigned cp)
            {
                if (cp < 0x80) out += static_cast<char>(cp);
                else if (cp < 0x800)
                {
                    out += static_cast<char>(0xC0 | (cp >> 6));
                    out += static_cast<char>(0x80 | (cp & 0x3F));
                }
                else if (cp < 0x10000)
                {
                    out += static_cast<char>(0xE0 | (cp >> 12));
                    out += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
                    out += static_cast<char>(0x80 | (cp & 0x3F));
                }
                else
                {
                    out += static_cast<char>(0xF0 | (cp >> 18));
                    out += static_cast<char>(0x80 | ((cp >> 12) & 0x3F));
                    out += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
                    out += static_cast<char>(0x80 | (cp & 0x3F));
                }
            }

            bool Hex4(unsigned& v)
            {
                if (end - p < 4) return false;
                v = 0;
                for (int i = 0; i < 4; ++i)
                {
                    char c = *p++;
                    v <<= 4;
                    if (c >= '0' && c <= '9') v |= c - '0';
                    else if (c >= 'a' && c <= 'f') v |= c - 'a' + 10;
                    else if (c >= 'A' && c <= 'F') v |= c - 'A' + 10;
                    else return false;
                }
                return true;
            }

            bool String(std::string& out)
            {
                if (p >= end || *p != '"') return false;
                ++p;
                while (p < end)
                {
                    char c = *p++;
                    if (c == '"') return true;
                    if (c != '\\')
                    {
                        out += c;
                        continue;
                    }
                    if (p >= end) return false;
                    char e = *p++;
                    switch (e)
                    {
                    case '"': out += '"'; break;
                    case '\\': out += '\\'; break;
                    case '/': out += '/'; break;
                    case 'b': out += '\b'; break;
                    case 'f': out += '\f'; break;
                    case 'n': out += '\n'; break;
                    case 'r': out += '\r'; break;
                    case 't': out += '\t'; break;
                    case 'u':
                    {
                        unsigned cp;
                        if (!Hex4(cp)) return false;
                        if (cp >= 0xD800 && cp <= 0xDBFF && end - p >= 6 && p[0] == '\\' && p[1] == 'u')
                        {
                            p += 2;
                            unsigned lo;
                            if (!Hex4(lo)) return false;
                            if (lo >= 0xDC00 && lo <= 0xDFFF) cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00);
                            else cp = 0xFFFD;
                        }
                        AppendUtf8(out, cp);
                        break;
                    }
                    default: return false;
                    }
                }
                return false;
            }

            bool Skip(int depth)
            {
                if (depth > 16) return false;
                SkipWs();
                if (p >= end) return false;
                if (*p == '"')
                {
                    std::string tmp;
                    return String(tmp);
                }
                if (*p == '{' || *p == '[')
                {
                    char close = *p == '{' ? '}' : ']';
                    bool obj = *p == '{';
                    ++p;
                    SkipWs();
                    if (p < end && *p == close)
                    {
                        ++p;
                        return true;
                    }
                    while (p < end)
                    {
                        if (obj)
                        {
                            std::string k;
                            SkipWs();
                            if (!String(k)) return false;
                            SkipWs();
                            if (p >= end || *p++ != ':') return false;
                        }
                        if (!Skip(depth + 1)) return false;
                        SkipWs();
                        if (p < end && *p == ',')
                        {
                            ++p;
                            continue;
                        }
                        if (p < end && *p == close)
                        {
                            ++p;
                            return true;
                        }
                        return false;
                    }
                    return false;
                }
                while (p < end && *p != ',' && *p != '}' && *p != ']' && *p != ' ' && *p != '\n' && *p != '\r' && *p != '\t') ++p;
                return true;
            }

            bool ValueInto(Value& v)
            {
                SkipWs();
                if (p >= end) return false;
                char c = *p;
                if (c == '"')
                {
                    v.type = Value::Type::String;
                    return String(v.s);
                }
                if (c == '{' || c == '[') return Skip(1); // nested: ignored, stays Null
                if (end - p >= 4 && std::string(p, 4) == "true")
                {
                    v.type = Value::Type::Bool;
                    v.b = true;
                    p += 4;
                    return true;
                }
                if (end - p >= 5 && std::string(p, 5) == "false")
                {
                    v.type = Value::Type::Bool;
                    v.b = false;
                    p += 5;
                    return true;
                }
                if (end - p >= 4 && std::string(p, 4) == "null")
                {
                    p += 4;
                    return true;
                }
                char* stop = nullptr;
                std::string num(p, std::min<size_t>(static_cast<size_t>(end - p), 64));
                double d = std::strtod(num.c_str(), &stop);
                if (stop == num.c_str()) return false;
                p += stop - num.c_str();
                v.type = Value::Type::Number;
                v.n = d;
                return true;
            }
        };
    }

    bool ParseObject(const std::string& text, Object& out)
    {
        Parser ps{text.data(), text.data() + text.size()};
        ps.SkipWs();
        if (ps.p >= ps.end || *ps.p != '{') return false;
        ++ps.p;
        ps.SkipWs();
        if (ps.p < ps.end && *ps.p == '}') return true;
        while (ps.p < ps.end)
        {
            std::string key;
            ps.SkipWs();
            if (!ps.String(key)) return false;
            ps.SkipWs();
            if (ps.p >= ps.end || *ps.p++ != ':') return false;
            Value v;
            if (!ps.ValueInto(v)) return false;
            out[key] = std::move(v);
            ps.SkipWs();
            if (ps.p < ps.end && *ps.p == ',')
            {
                ++ps.p;
                continue;
            }
            if (ps.p < ps.end && *ps.p == '}') return true;
            return false;
        }
        return false;
    }

    std::string Escape(const std::string& s)
    {
        std::string out;
        out.reserve(s.size() + 8);
        for (unsigned char c : s)
        {
            switch (c)
            {
            case '"': out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\n': out += "\\n"; break;
            case '\r': out += "\\r"; break;
            case '\t': out += "\\t"; break;
            default:
                if (c < 0x20)
                {
                    char buf[8];
                    std::snprintf(buf, sizeof(buf), "\\u%04x", c);
                    out += buf;
                }
                else out += static_cast<char>(c);
            }
        }
        return out;
    }

    std::string GetString(const Object& o, const char* key, const char* def)
    {
        auto it = o.find(key);
        if (it == o.end()) return def;
        if (it->second.type == Value::Type::String) return it->second.s;
        return def;
    }

    double GetNumber(const Object& o, const char* key, double def)
    {
        auto it = o.find(key);
        if (it == o.end()) return def;
        if (it->second.type == Value::Type::Number) return it->second.n;
        if (it->second.type == Value::Type::Bool) return it->second.b ? 1 : 0;
        return def;
    }

    bool GetBool(const Object& o, const char* key, bool def)
    {
        auto it = o.find(key);
        if (it == o.end()) return def;
        if (it->second.type == Value::Type::Bool) return it->second.b;
        if (it->second.type == Value::Type::Number) return it->second.n != 0;
        return def;
    }

    Writer::Writer(const char* type)
    {
        m_out = "{\"v\":1,\"type\":\"";
        m_out += type;
        m_out += '"';
    }

    Writer& Writer::Str(const char* key, const std::string& value)
    {
        m_out += ",\"";
        m_out += key;
        m_out += "\":\"";
        m_out += Escape(value);
        m_out += '"';
        return *this;
    }

    Writer& Writer::Num(const char* key, double value)
    {
        char buf[64];
        std::snprintf(buf, sizeof(buf), ",\"%s\":%.6g", key, value);
        m_out += buf;
        return *this;
    }

    Writer& Writer::Int(const char* key, long long value)
    {
        char buf[64];
        std::snprintf(buf, sizeof(buf), ",\"%s\":%lld", key, value);
        m_out += buf;
        return *this;
    }

    Writer& Writer::Bool(const char* key, bool value)
    {
        m_out += ",\"";
        m_out += key;
        m_out += value ? "\":true" : "\":false";
        return *this;
    }

    std::string Writer::Finish()
    {
        return m_out + "}";
    }
}
