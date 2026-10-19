#include "log.h"

#include <windows.h>

#include <cstdarg>
#include <cstdio>
#include <mutex>

namespace mdr::log
{
    namespace
    {
        std::mutex g_mutex;
        FILE* g_file = nullptr;
        bool g_debug = false;

        void Write(const char* level, const char* fmt, va_list args)
        {
            std::lock_guard lock(g_mutex);
            if (!g_file) return;
            SYSTEMTIME t;
            GetLocalTime(&t);
            std::fprintf(g_file, "%02u:%02u:%02u.%03u [%s] ", t.wHour, t.wMinute, t.wSecond, t.wMilliseconds, level);
            std::vfprintf(g_file, fmt, args);
            std::fputc('\n', g_file);
            std::fflush(g_file);
        }
    }

    void Init(const std::string& gameDir, bool debug)
    {
        std::lock_guard lock(g_mutex);
        g_debug = debug;
        if (g_file) return;
        std::string path = gameDir;
        if (!path.empty() && path.back() != '\\' && path.back() != '/') path += '\\';
        path += "MojaveDesktopRadio.log";
        fopen_s(&g_file, path.c_str(), "w");
    }

    void Info(const char* fmt, ...)
    {
        va_list args;
        va_start(args, fmt);
        Write("INF", fmt, args);
        va_end(args);
    }

    void Debug(const char* fmt, ...)
    {
        if (!g_debug) return;
        va_list args;
        va_start(args, fmt);
        Write("DBG", fmt, args);
        va_end(args);
    }

    void Close()
    {
        std::lock_guard lock(g_mutex);
        if (g_file) std::fclose(g_file);
        g_file = nullptr;
    }
}
