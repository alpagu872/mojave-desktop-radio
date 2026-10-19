#pragma once
#include <string>

namespace mdr::text
{
    // UTF-8 (from the BridgeHost) → the game's 8-bit code page (1254 for the Turkish patch, 1252 vanilla).
    // Characters that do not exist in the code page get Windows' best-fit mapping or '?'.
    // Control characters are removed and the result is cut to maxChars (with "...").
    std::string Utf8ToGame(const std::string& utf8, unsigned codePage, size_t maxChars);

    // 8-bit game text (from scripts / INI) → UTF-8 for logging and JSON.
    std::string GameToUtf8(const std::string& game, unsigned codePage);

    std::string Trim(const std::string& s);
    std::string ToLower(std::string s);
}
