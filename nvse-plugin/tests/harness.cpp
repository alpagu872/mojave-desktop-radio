// Offline test harness for MojaveDesktopRadio.dll (32-bit).
//
// Emulates just enough of xNVSE (interfaces, main-loop messages, events) and of the game
// (the Pip-Boy radio pointer at 0x011DD42C) to drive the real plugin DLL against a running
// BridgeHost — no game needed.
//
//   mdr_plugin_harness.exe <path\MojaveDesktopRadio.dll> [seconds] [--autoplay]
//
// Timeline: t=1s init + config, t=3s station ON, t=9s refresh, t=12s station OFF, t=15s exit.

#include "../src/nvse_api.h"

#include <windows.h>

#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <map>
#include <string>

namespace
{
    NVSEMessagingInterface::EventCallback g_listener = nullptr;
    std::map<std::string, NVSEEventManagerInterface::NativeEventHandler> g_native;
    ULONGLONG g_t0 = 0;
    int g_stateDispatches = 0;
    int g_dispatchesDuringLoad = 0;
    bool g_inLoad = false;

    double Now() { return (GetTickCount64() - g_t0) / 1000.0; }

    // The plugin hands the game Windows-1254 text; show it as UTF-8 on the console.
    std::string FromGame(const char* s)
    {
        if (!s || !*s) return {};
        int wn = MultiByteToWideChar(1254, 0, s, -1, nullptr, 0);
        std::wstring w(static_cast<size_t>(wn), L'\0');
        MultiByteToWideChar(1254, 0, s, -1, w.data(), wn);
        int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), -1, nullptr, 0, nullptr, nullptr);
        std::string out(static_cast<size_t>(n), '\0');
        WideCharToMultiByte(CP_UTF8, 0, w.c_str(), -1, out.data(), n, nullptr, nullptr);
        out.resize(std::strlen(out.c_str()));
        return out;
    }

    bool RegisterListener(PluginHandle, const char* sender, NVSEMessagingInterface::EventCallback cb)
    {
        std::printf("[%5.2f] RegisterListener(%s)\n", Now(), sender);
        g_listener = cb;
        return true;
    }

    bool DispatchMsg(PluginHandle, UInt32, void*, UInt32, const char*) { return true; }

    bool RegisterEvent(const char* name, UInt8 n, NVSEEventManagerInterface::ParamType*, NVSEEventManagerInterface::EventFlags flags)
    {
        std::printf("[%5.2f] RegisterEvent(%s, %u params, flags %u)\n", Now(), name, n, static_cast<unsigned>(flags));
        return true;
    }

    // Same signature as xNVSE: (const char* eventName, TESObjectREFR* thisObj, ...)
    bool DispatchEventImpl(const char* name, TESObjectREFR* thisObj, ...)
    {
        (void)thisObj;
        va_list a;
        va_start(a, thisObj);
        if (std::string(name) == "MDR:State")
        {
            int active = static_cast<int>(reinterpret_cast<intptr_t>(va_arg(a, void*)));
            int playing = static_cast<int>(reinterpret_cast<intptr_t>(va_arg(a, void*)));
            int rev = static_cast<int>(reinterpret_cast<intptr_t>(va_arg(a, void*)));
            std::string s[9];
            for (auto& p : s) p = FromGame(static_cast<const char*>(va_arg(a, void*)));
            ++g_stateDispatches;
            if (g_inLoad) ++g_dispatchesDuringLoad;
            static int s_lastRev = -1;
            if (rev != s_lastRev) // the panel script only writes when the revision changes; print the same
            {
                s_lastRev = rev;
                std::printf("[%5.2f] MDR:State rev=%d active=%d playing=%d | %s | %s | %s | %s | %s | %s\n", Now(), rev, active, playing,
                    s[0].c_str(), s[1].c_str(), s[2].c_str(), s[3].c_str(), s[4].c_str(), s[5].c_str());
            }
        }
        else if (std::string(name) == "MDR:Notify")
        {
            std::printf("[%5.2f] MDR:Notify \"%s\"\n", Now(), FromGame(static_cast<const char*>(va_arg(a, void*))).c_str());
        }
        va_end(a);
        return true;
    }

    bool SetNative(const char* name, NVSEEventManagerInterface::NativeEventHandler h)
    {
        std::printf("[%5.2f] SetNativeEventHandler(%s)\n", Now(), name);
        g_native[name] = h;
        return true;
    }

    bool RemoveNative(const char*, NVSEEventManagerInterface::NativeEventHandler) { return true; }

    NVSEMessagingInterface g_messaging{4, RegisterListener, DispatchMsg};
    NVSEEventManagerInterface g_events{RegisterEvent, DispatchEventImpl, nullptr, SetNative, RemoveNative};
    char g_dir[MAX_PATH];

    void* QueryInterface(UInt32 id)
    {
        if (id == kInterface_Messaging) return &g_messaging;
        if (id == kInterface_EventManager) return &g_events;
        return nullptr;
    }

    PluginHandle GetHandle() { return 7; }
    const char* RuntimeDir() { return g_dir; }

    void Script(const char* cmd)
    {
        std::printf("[%5.2f] script: DispatchEventAlt \"MDR:Cmd\" \"%s\"\n", Now(), cmd);
        void* args[1] = {const_cast<char*>(cmd)};
        g_native["MDR:Cmd"](nullptr, args);
    }

    // DispatchEventAlt "MDR:Set" "<key>" <number>: xNVSE passes script numbers as float bits
    void ScriptSet(const char* key, float value)
    {
        void* args[2] = {const_cast<char*>(key), nullptr};
        std::memcpy(&args[1], &value, sizeof(float));
        g_native["MDR:Set"](nullptr, args);
    }

    void Message(UInt32 type)
    {
        NVSEMessagingInterface::Message m{"NVSE", type, 0, nullptr};
        g_listener(&m);
    }
}

int main(int argc, char** argv)
{
    if (argc < 2)
    {
        std::printf("usage: %s <MojaveDesktopRadio.dll> [seconds] [--autoplay]\n", argv[0]);
        return 1;
    }
    // --pipe NAME isolates the test from a real game session; with --autolaunch the BridgeHost is
    // started with --no-audio so it never touches audio devices or Spotify's routing.
    bool autoplay = false, autolaunch = false;
    std::string pipe;
    for (int i = 2; i < argc; ++i)
    {
        if (std::string(argv[i]) == "--autoplay") autoplay = true;
        if (std::string(argv[i]) == "--autolaunch") autolaunch = true;
        if (std::string(argv[i]) == "--pipe" && i + 1 < argc) pipe = argv[++i];
    }
    SetConsoleOutputCP(CP_UTF8);
    g_t0 = GetTickCount64();
    GetTempPathA(MAX_PATH, g_dir);

    // Fake game memory. In the game the plugin reads the RadioEntry* at 0x011DD42C; that address is
    // reserved by WOW64 in a normal 32-bit process, so the harness points the plugin at its own slot
    // through the [Debug] INI override. Layout: RadioEntry { TESObjectREFR* radioRef; ... }, refID at +0x0C.
    static void* slotStorage = nullptr;
    static void* radioEntry[8] = {};
    static unsigned char fakeRef[0x40] = {};
    *reinterpret_cast<UInt32*>(fakeRef + 0x0C) = 0x05000802; // MojaveDesktopRadio.esp at mod index 5
    auto slot = &slotStorage;

    {
        std::string dllPath = argv[1];
        std::string ini = dllPath.substr(0, dllPath.find_last_of("\\/") + 1) + "MojaveDesktopRadio.ini";
        FILE* f = nullptr;
        fopen_s(&f, ini.c_str(), "w");
        if (!f)
        {
            std::printf("cannot write %s\n", ini.c_str());
            return 1;
        }
        std::fprintf(f, "[General]\nbDebugLog=1\n%s%s\n[Bridge]\nbAutoLaunch=%d\niLaunchDelayMs=500\nsExtraArgs=%s\n[Debug]\niPipboyRadioAddress=0x%08X\n",
            pipe.empty() ? "" : "sPipeName=", pipe.c_str(), autolaunch ? 1 : 0, pipe.empty() ? "" : "--no-audio",
            static_cast<unsigned>(reinterpret_cast<uintptr_t>(slot)));
        std::fclose(f);
    }

    HMODULE dll = LoadLibraryA(argv[1]);
    if (!dll)
    {
        std::printf("LoadLibrary failed (%lu)\n", GetLastError());
        return 1;
    }
    auto query = reinterpret_cast<bool (*)(const NVSEInterface*, PluginInfo*)>(GetProcAddress(dll, "NVSEPlugin_Query"));
    auto load = reinterpret_cast<bool (*)(const NVSEInterface*)>(GetProcAddress(dll, "NVSEPlugin_Load"));
    if (!query || !load)
    {
        std::printf("exports missing\n");
        return 1;
    }

    NVSEInterface nvse{};
    nvse.nvseVersion = MAKE_NEW_VEGAS_VERSION(6, 4, 9);
    nvse.runtimeVersion = RUNTIME_VERSION_1_4_0_525;
    nvse.QueryInterface = QueryInterface;
    nvse.GetPluginHandle = GetHandle;
    nvse.GetRuntimeDirectory = RuntimeDir;

    PluginInfo info{};
    const bool q = query(&nvse, &info);
    std::printf("[%5.2f] Query -> %d (name=%s version=%u)\n", Now(), q, info.name, info.version);
    if (!q || !load(&nvse))
    {
        std::printf("plugin refused to load\n");
        return 1;
    }

    const double seconds = (argc > 2 && std::atof(argv[2]) > 0) ? std::atof(argv[2]) : 20.0;
    bool inited = false, on = false, refreshed = false, off = false, presetChanged = false;
    bool loadStarted = false, retuned = false;
    while (Now() < seconds)
    {
        const double t = Now();
        if (!inited && t > 1.0)
        {
            inited = true;
            Script("init 5");
            std::printf("[%5.2f] script: PushConfig.gek (15 x MDR:Set)\n", Now());
            const struct { const char* k; float v; } values[] = {
                {"fx", 1}, {"preset", 1}, {"volume", 100}, {"static", 50}, {"saturation", 50}, {"custom", 0},
                {"lowpass", 3300}, {"highpass", 280}, {"width", 20}, {"fade", 250}, {"tuning", 1},
                {"autopause", autoplay ? 1.f : 0.f}, {"autoplay", autoplay ? 1.f : 0.f}, {"pausemenu", 0}, {"trackmsg", 1}};
            for (const auto& kv : values) ScriptSet(kv.k, kv.v);
        }
        if (!presetChanged && t > 6.0)
        {
            presetChanged = true;
            std::printf("[%5.2f] script: CycleFx -> MDR:Set \"preset\" 2\n", t);
            ScriptSet("fx", 1);
            ScriptSet("preset", 2);
        }
        if (!on && t > 3.0)
        {
            on = true;
            radioEntry[0] = fakeRef;
            *slot = radioEntry;
            std::printf("[%5.2f] game: Pip-Boy radio tuned to Mojave Desktop Radio\n", t);
        }
        if (!refreshed && t > 9.0)
        {
            refreshed = true;
            Script("refresh");
        }
        if (!off && t > 12.0)
        {
            off = true;
            *slot = nullptr;
            std::printf("[%5.2f] game: Pip-Boy radio turned off (e.g. the player died)\n", t);
        }
        // save load after death: PreLoadGame, ~1 s of loading frames, the ln_ script ("init"), PostLoadGame
        if (!loadStarted && t > 13.0)
        {
            loadStarted = g_inLoad = true;
            std::printf("[%5.2f] game: loading a save (radio stays off in that save)\n", t);
            Message(NVSEMessagingInterface::kMessage_PreLoadGame);
        }
        if (g_inLoad && t > 14.0)
        {
            Script("init 5");
            g_inLoad = false;
            Message(NVSEMessagingInterface::kMessage_PostLoadGame);
            std::printf("[%5.2f] game: load finished\n", t);
        }
        if (!retuned && t > 16.0)
        {
            retuned = true;
            *slot = radioEntry;
            std::printf("[%5.2f] game: player tunes Mojave Desktop Radio again\n", t);
        }
        Message(NVSEMessagingInterface::kMessage_MainGameLoop);
        Sleep(16);
    }

    Message(NVSEMessagingInterface::kMessage_ExitGame);
    std::printf("[%5.2f] exit — MDR:State dispatched %d times (%.1f/s), %d during the load\n", Now(), g_stateDispatches,
        g_stateDispatches / seconds, g_dispatchesDuringLoad);
    return g_dispatchesDuringLoad == 0 ? 0 : 3;
}
