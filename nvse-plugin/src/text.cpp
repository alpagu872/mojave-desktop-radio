#include "text.h"

#include <windows.h>

#include <algorithm>
#include <cctype>

namespace mdr::text
{
    std::string Utf8ToGame(const std::string& utf8, unsigned codePage, size_t maxChars)
    {
        if (utf8.empty()) return {};
        int wlen = MultiByteToWideChar(CP_UTF8, 0, utf8.data(), static_cast<int>(utf8.size()), nullptr, 0);
        if (wlen <= 0) return {};
        std::wstring w(static_cast<size_t>(wlen), L'\0');
        MultiByteToWideChar(CP_UTF8, 0, utf8.data(), static_cast<int>(utf8.size()), w.data(), wlen);

        // strip control characters, normalise whitespace
        std::wstring clean;
        clean.reserve(w.size());
        for (wchar_t c : w)
        {
            if (c < 0x20 || (c >= 0x7F && c < 0xA0)) c = L' ';
            if (c == L' ' && !clean.empty() && clean.back() == L' ') continue;
            clean += c;
        }

        if (maxChars > 3 && clean.size() > maxChars)
        {
            clean.resize(maxChars - 3);
            while (!clean.empty() && clean.back() == L' ') clean.pop_back();
            clean += L"...";
        }

        int len = WideCharToMultiByte(codePage, 0, clean.data(), static_cast<int>(clean.size()), nullptr, 0, "?", nullptr);
        if (len <= 0) return {};
        std::string out(static_cast<size_t>(len), '\0');
        WideCharToMultiByte(codePage, 0, clean.data(), static_cast<int>(clean.size()), out.data(), len, "?", nullptr);
        return out;
    }

    std::string GameToUtf8(const std::string& game, unsigned codePage)
    {
        if (game.empty()) return {};
        int wlen = MultiByteToWideChar(codePage, 0, game.data(), static_cast<int>(game.size()), nullptr, 0);
        if (wlen <= 0) return {};
        std::wstring w(static_cast<size_t>(wlen), L'\0');
        MultiByteToWideChar(codePage, 0, game.data(), static_cast<int>(game.size()), w.data(), wlen);
        int len = WideCharToMultiByte(CP_UTF8, 0, w.data(), wlen, nullptr, 0, nullptr, nullptr);
        std::string out(static_cast<size_t>(len), '\0');
        WideCharToMultiByte(CP_UTF8, 0, w.data(), wlen, out.data(), len, nullptr, nullptr);
        return out;
    }

    std::string Trim(const std::string& s)
    {
        size_t b = 0, e = s.size();
        while (b < e && std::isspace(static_cast<unsigned char>(s[b]))) ++b;
        while (e > b && std::isspace(static_cast<unsigned char>(s[e - 1]))) --e;
        return s.substr(b, e - b);
    }

    std::string ToLower(std::string s)
    {
        std::transform(s.begin(), s.end(), s.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
        return s;
    }
}
