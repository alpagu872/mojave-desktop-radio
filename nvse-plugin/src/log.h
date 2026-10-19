#pragma once
#include <string>

namespace mdr::log
{
    // Opens "<game dir>\MojaveDesktopRadio.log" (truncated each start, like other xNVSE plugin logs).
    void Init(const std::string& gameDir, bool debug);
    void Info(const char* fmt, ...);
    void Debug(const char* fmt, ...);
    void Close();
}
