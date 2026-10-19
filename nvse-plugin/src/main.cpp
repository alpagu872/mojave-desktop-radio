// Mojave Desktop Radio â€” xNVSE plugin
//
// Responsibilities (all non-blocking for the game's main thread):
//  * detect when the Pip-Boy radio is tuned to the MDR station (one pointer read per frame)
//  * forward station / commands / settings to the BridgeHost over a named pipe (background thread)
//  * push display strings to the Pip-Boy panel through the xNVSE event "MDR:State"
//
// No script commands (opcodes) are registered: scripts talk to us via DispatchEventAlt "MDR:Cmd".

#include "log.h"
#include "mini_json.h"
#include "nvse_api.h"
#include "pipe_client.h"
#include "settings.h"
#include "text.h"

#include <windows.h>

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <map>
#include <string>

namespace
{
    constexpr UInt32 kPluginVersion = 2; // GetPluginVersion "MojaveDesktopRadio"
    constexpr const char* kPluginVersionString = "0.2.0";
    // RadioEntry* at 0x011DD42C (1.4.0.525), see JIP LN GameGlobals::PipboyRadio; configurable only for the test harness.
    constexpr UInt32 kRefIdOffset = 0x0C; // TESForm::refID

    using EM = NVSEEventManagerInterface;

    PluginHandle g_handle = kPluginHandle_Invalid;
    NVSEMessagingInterface* g_messaging = nullptr;
    EM* g_events = nullptr;
    std::string g_gameDir;
    std::wstring g_pluginDirW; // ...\Data\NVSE\Plugins\ (with trailing backslash)
    mdr::Settings g_cfg;
    mdr::PipeClient g_pipe;

    // game state
    bool g_gameLoaded = false;
    bool g_loading = false;
    UInt32 g_stationRefId = 0;
    bool g_stationActive = false;
    unsigned g_seenEpoch = 0;
    bool g_wasConnected = false;
    long long g_nextCmdId = 1;

    // config pushed by the script (re-sent on every reconnect)
    std::map<std::string, double> g_config;
    bool g_configDirty = false;
    ULONGLONG g_configChangedTick = 0;
    std::string g_configJson;
    bool g_trackMessages = true;
    ULONGLONG g_initTick = 0;
    bool g_configWarned = false;

    // bridge state (UTF-8)
    struct BridgeState
    {
        bool valid = false;
        std::string player = "not_running";
        bool playing = false;
        std::string artist, title, fx = "Clean", fxRequested = "Clean", route = "off", routeDetail;
        bool fxEnabled = false, fxActive = false;
    } g_bridge;

    std::string g_lastTrackKey;
    bool g_uiDirty = true;
    ULONGLONG g_lastUiDispatch = 0;

    // bridge auto-launch
    ULONGLONG g_firstLoadTick = 0;
    constexpr int kMaxLaunchAttempts = 5;
    ULONGLONG g_launchTick = 0;
    ULONGLONG g_nextLaunchTick = 0;
    int g_launchAttempts = 0;

    // ---- helpers -------------------------------------------------------------------------------

    UInt32 ReadPipboyRadioRefId(UInt32 address)
    {
        __try
        {
            auto entry = *reinterpret_cast<void**>(address);
            if (!entry) return 0;
            auto ref = *reinterpret_cast<void**>(entry); // RadioEntry::radioRef
            if (!ref) return 0;
            return *reinterpret_cast<UInt32*>(reinterpret_cast<char*>(ref) + kRefIdOffset);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            return 0;
        }
    }

    std::string Game(const std::string& utf8)
    {
        return mdr::text::Utf8ToGame(utf8, g_cfg.codePage, g_cfg.maxTitleChars);
    }

    std::string FxName(const std::string& key)
    {
        auto k = mdr::text::ToLower(key);
        if (k == "mojaveradio") return g_cfg.fxMojave;
        if (k == "wastelandradio") return g_cfg.fxWasteland;
        if (k == "damagedradio") return g_cfg.fxDamaged;
        return g_cfg.fxClean;
    }

    std::string ReplaceToken(std::string fmt, const std::string& value)
    {
        auto pos = fmt.find("%s");
        if (pos != std::string::npos) fmt.replace(pos, 2, value);
        return fmt;
    }

    void Notify(const std::string& gameText)
    {
        if (!g_events || !g_gameLoaded || gameText.empty()) return;
        g_events->DispatchEvent("MDR:Notify", nullptr, gameText.c_str());
    }

    void SendStation()
    {
        g_pipe.Send(mdr::json::Writer("station").Bool("active", g_stationActive).Finish());
    }

    void SendCommand(const char* action)
    {
        if (!g_pipe.IsConnected())
        {
            Notify(g_cfg.msgBridgeOffline);
            return;
        }
        g_pipe.Send(mdr::json::Writer("cmd").Int("id", g_nextCmdId++).Str("action", action).Finish());
    }

    // ---- display ---------------------------------------------------------------------------------

    void DispatchUi()
    {
        if (!g_events) return;
        const bool connected = g_pipe.IsConnected();
        std::string signal, status, artist, track, fx, playLabel;

        if (!connected)
        {
            signal = g_cfg.signalBridgeOffline;
            status = g_cfg.playerNotFound;
        }
        else if (!g_bridge.valid)
        {
            signal = g_cfg.signalConnected; // first state message is on its way
        }
        else if (g_bridge.player == "not_running")
        {
            signal = g_cfg.signalLost;
            status = g_cfg.playerNotFound;
        }
        else
        {
            signal = g_cfg.signalConnected;
            if (g_bridge.player == "playing") status = g_cfg.statusPlaying;
            else if (g_bridge.player == "paused") status = g_cfg.statusPaused;
            else if (g_bridge.player == "no_session") status = g_cfg.playerNoSession;
            else status = g_cfg.statusStopped;

            if (!g_bridge.artist.empty()) artist = g_cfg.artistPrefix + Game(g_bridge.artist);
            if (!g_bridge.title.empty()) track = g_cfg.trackPrefix + Game(g_bridge.title);
        }

        if (connected && g_bridge.valid)
        {
            if (!g_bridge.fxEnabled) fx = g_cfg.fxPrefix + g_cfg.fxOff;
            else
            {
                fx = g_cfg.fxPrefix + FxName(g_bridge.fxRequested);
                if (mdr::text::ToLower(g_bridge.fxRequested) != "clean")
                {
                    if (g_bridge.route == "unavailable") fx += g_cfg.fxUnavailableSuffix;
                    else if (g_bridge.route == "pending") fx += g_cfg.fxPendingSuffix;
                }
            }
        }

        playLabel = g_bridge.playing ? g_cfg.labelPause : g_cfg.labelPlay;

        g_events->DispatchEvent("MDR:State", nullptr,
            reinterpret_cast<void*>(static_cast<intptr_t>(g_stationActive ? 1 : 0)),
            reinterpret_cast<void*>(static_cast<intptr_t>(g_bridge.playing ? 1 : 0)),
            signal.c_str(), status.c_str(), artist.c_str(), track.c_str(), fx.c_str(), playLabel.c_str(),
            g_cfg.labelNext.c_str(), g_cfg.labelPrev.c_str(), g_cfg.labelFx.c_str());

        g_uiDirty = false;
        g_lastUiDispatch = GetTickCount64();
    }

    // ---- inbound from BridgeHost ----------------------------------------------------------------

    void HandleInbound(const std::string& line)
    {
        mdr::json::Object o;
        if (!mdr::json::ParseObject(line, o))
        {
            mdr::log::Info("unreadable message from BridgeHost: %.120s", line.c_str());
            return;
        }

        const auto type = mdr::json::GetString(o, "type");
        if (type == "_disconnected")
        {
            g_bridge = BridgeState{};
            g_uiDirty = true;
            return;
        }

        const int version = static_cast<int>(mdr::json::GetNumber(o, "v", 0));
        if (version != 1)
        {
            mdr::log::Info("ignoring message with protocol v%d", version);
            return;
        }

        if (type == "welcome")
        {
            mdr::log::Info("BridgeHost %s (protocol %d)", mdr::json::GetString(o, "serverVersion").c_str(),
                static_cast<int>(mdr::json::GetNumber(o, "protocol", 0)));
        }
        else if (type == "state")
        {
            BridgeState s;
            s.valid = true;
            s.player = mdr::json::GetString(o, "player", "not_running");
            s.playing = mdr::json::GetBool(o, "playing");
            s.artist = mdr::json::GetString(o, "artist");
            s.title = mdr::json::GetString(o, "title");
            s.fx = mdr::json::GetString(o, "fx", "Clean");
            s.fxRequested = mdr::json::GetString(o, "fxRequested", s.fx.c_str());
            s.fxEnabled = mdr::json::GetBool(o, "fxEnabled");
            s.fxActive = mdr::json::GetBool(o, "fxActive");
            s.route = mdr::json::GetString(o, "route", "off");
            s.routeDetail = mdr::json::GetString(o, "routeDetail");

            const bool lostPlayer = g_bridge.valid && g_bridge.player != "not_running" && s.player == "not_running";
            g_bridge = std::move(s);
            g_uiDirty = true;

            if (g_stationActive && lostPlayer) Notify(g_cfg.msgPlayerNotFound);

            const std::string key = g_bridge.artist + "\x1f" + g_bridge.title;
            if (key != g_lastTrackKey)
            {
                g_lastTrackKey = key;
                if (g_stationActive && g_trackMessages && g_bridge.playing && !g_bridge.title.empty())
                {
                    const std::string label = g_bridge.artist.empty() ? g_bridge.title : g_bridge.artist + " - " + g_bridge.title;
                    Notify(ReplaceToken(g_cfg.nowPlayingFormat, mdr::text::Utf8ToGame(label, g_cfg.codePage, 60)));
                }
            }
        }
        else if (type == "error")
        {
            const auto code = mdr::json::GetString(o, "code");
            mdr::log::Info("BridgeHost error %s: %s", code.c_str(), mdr::json::GetString(o, "message").c_str());
            if (code == "spotify_not_running") Notify(g_cfg.msgPlayerNotFound);
        }
        // "ack", "pong": nothing to do
    }

    // ---- config from the script ------------------------------------------------------------------

    // MCM values arrive one by one (DispatchEventAlt "MDR:Set" "<key>" <number>, see PushConfig.gek);
    // they are collected here and sent to the BridgeHost as one "config" message a moment later.
    void SetConfigValue(const std::string& key, double value)
    {
        const auto k = mdr::text::ToLower(mdr::text::Trim(key));
        if (k.empty()) return;
        g_config[k] = value;
        if (k == "trackmsg") g_trackMessages = value != 0;
        g_configDirty = true;
        g_configChangedTick = GetTickCount64();
    }

    std::string BuildConfigJson()
    {
        mdr::json::Writer w("config");
        for (const auto& [k, v] : g_config)
        {
            if (k == "fx") w.Bool("fxEnabled", v != 0);
            else if (k == "preset")
            {
                static const char* names[] = {"Clean", "Mojave", "Wasteland", "Damaged"};
                int i = static_cast<int>(v);
                w.Str("preset", names[(i >= 0 && i <= 3) ? i : 1]);
            }
            else if (k == "volume") w.Num("volume", v / 100.0);
            else if (k == "static") w.Num("static", v / 100.0);
            else if (k == "saturation") w.Num("saturation", v / 100.0);
            else if (k == "custom") w.Bool("customTone", v != 0);
            else if (k == "lowpass") w.Num("lowPassHz", v);
            else if (k == "highpass") w.Num("highPassHz", v);
            else if (k == "width") w.Num("stereoWidth", v / 100.0);
            else if (k == "fade") w.Num("fadeMs", v);
            else if (k == "tuning") w.Bool("tuningFx", v != 0);
            else if (k == "autopause") w.Bool("autoPause", v != 0);
            else if (k == "autoplay") w.Bool("autoPlay", v != 0);
            else if (k == "pausemenu") w.Num("pauseMenu", v);
            // "trackmsg" is handled by the plugin itself; unknown keys are ignored
        }
        return w.Finish();
    }

    void FlushConfig()
    {
        g_configDirty = false;
        g_configJson = BuildConfigJson();
        mdr::log::Info("config: %s", g_configJson.c_str());
        g_pipe.Send(g_configJson);
    }

    // legacy form: "fx=1;preset=1;volume=100;..."
    void ApplyConfigString(const std::string& kv)
    {
        size_t start = 0;
        while (start < kv.size())
        {
            size_t end = kv.find(';', start);
            if (end == std::string::npos) end = kv.size();
            std::string pair = kv.substr(start, end - start);
            start = end + 1;
            size_t eq = pair.find('=');
            if (eq == std::string::npos) continue;
            SetConfigValue(pair.substr(0, eq), std::atof(mdr::text::Trim(pair.substr(eq + 1)).c_str()));
        }
    }

    // DispatchEventAlt "MDR:Set" "<key>" <number>
    void OnScriptSet(TESObjectREFR*, void* parameters)
    {
        auto args = static_cast<void**>(parameters);
        if (!args || !args[0]) return;
        float value;
        std::memcpy(&value, &args[1], sizeof(float)); // script numbers arrive as float bits
        SetConfigValue(static_cast<const char*>(args[0]), value);
    }

    // ---- bridge auto-launch -----------------------------------------------------------------------

    std::wstring Widen(const std::string& s)
    {
        if (s.empty()) return {};
        int n = MultiByteToWideChar(CP_ACP, 0, s.data(), static_cast<int>(s.size()), nullptr, 0);
        std::wstring w(static_cast<size_t>(n), L'\0');
        MultiByteToWideChar(CP_ACP, 0, s.data(), static_cast<int>(s.size()), w.data(), n);
        return w;
    }

    bool FileExists(const std::wstring& path)
    {
        const DWORD a = GetFileAttributesW(path.c_str());
        return a != INVALID_FILE_ATTRIBUTES && !(a & FILE_ATTRIBUTE_DIRECTORY);
    }

    // Search order: [Bridge] sPath â†’ Data\MojaveDesktopRadio\BridgeHost\ (shipped in the mod archive)
    //               â†’ %LOCALAPPDATA%\MojaveDesktopRadio\app\ (manual install)
    std::wstring FindBridgeExecutable()
    {
        if (!g_cfg.bridgePath.empty())
        {
            std::wstring p = Widen(g_cfg.bridgePath);
            if (FileExists(p)) return p;
            mdr::log::Info("BridgeHost: [Bridge] sPath does not exist, trying the default locations");
        }

        const std::wstring dataDir = g_pluginDirW + L"..\\..\\"; // Data\NVSE\Plugins\ -> Data\ (virtual under MO2)
        std::wstring p = dataDir + L"MojaveDesktopRadio\\BridgeHost\\MojaveDesktopRadio.exe";
        if (FileExists(p)) return p;

        wchar_t local[MAX_PATH];
        DWORD n = GetEnvironmentVariableW(L"LOCALAPPDATA", local, MAX_PATH);
        if (n > 0 && n < MAX_PATH)
        {
            p = std::wstring(local) + L"\\MojaveDesktopRadio\\app\\MojaveDesktopRadio.exe";
            if (FileExists(p)) return p;
        }
        return {};
    }

    void TryLaunchBridge()
    {
        ++g_launchAttempts;
        g_nextLaunchTick = GetTickCount64() + 15000; // retry while still disconnected (e.g. after a quick game restart)
        std::wstring path = FindBridgeExecutable();
        if (path.empty())
        {
            mdr::log::Info("BridgeHost auto-launch: MojaveDesktopRadio.exe not found (see README, [Bridge] sPath)");
            g_launchAttempts = kMaxLaunchAttempts;
            return;
        }

        // normalise "..\" so the working directory and the process image path are clean
        wchar_t full[MAX_PATH];
        if (GetFullPathNameW(path.c_str(), MAX_PATH, full, nullptr)) path = full;

        // pass our pipe name so both sides always agree, plus optional extra arguments
        std::wstring cmd = L"\"" + path + L"\" --game-pid " + std::to_wstring(GetCurrentProcessId()) +
                           L" --pipe " + Widen(g_cfg.pipeName);
        if (!g_cfg.bridgeExtraArgs.empty()) cmd += L" " + Widen(g_cfg.bridgeExtraArgs);
        std::wstring dir = path.substr(0, path.find_last_of(L"\\/"));
        STARTUPINFOW si{sizeof(si)};
        PROCESS_INFORMATION pi{};
        if (CreateProcessW(path.c_str(), cmd.data(), nullptr, nullptr, FALSE,
                DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP, nullptr, dir.c_str(), &si, &pi))
        {
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            g_launchTick = GetTickCount64();
            mdr::log::Info("BridgeHost launched: %ls", path.c_str());
        }
        else
        {
            mdr::log::Info("BridgeHost auto-launch failed (error %lu)", GetLastError());
        }
    }

    // ---- script â†’ plugin --------------------------------------------------------------------------

    // DispatchEventAlt "MDR:Cmd" "<verb> [args]"
    void OnScriptCommand(TESObjectREFR*, void* parameters)
    {
        auto args = static_cast<void**>(parameters);
        auto raw = args ? static_cast<const char*>(args[0]) : nullptr;
        if (!raw) return;
        const std::string line = mdr::text::Trim(raw);
        const auto space = line.find(' ');
        const std::string verb = mdr::text::ToLower(line.substr(0, space));
        const std::string rest = space == std::string::npos ? "" : mdr::text::Trim(line.substr(space + 1));
        mdr::log::Debug("script: %s", line.c_str());

        if (verb == "init")
        {
            const int modIndex = std::atoi(rest.c_str());
            g_stationRefId = (modIndex >= 0 && modIndex < 0xFF) ? ((static_cast<UInt32>(modIndex) << 24) | g_cfg.stationLocalId) : 0;
            g_gameLoaded = true;
            g_loading = false;
            if (!g_firstLoadTick) g_firstLoadTick = GetTickCount64();
            if (!g_initTick) g_initTick = GetTickCount64();
            g_uiDirty = true;
            mdr::log::Info("station reference %08X (mod index %d)", g_stationRefId, modIndex);
        }
        else if (verb == "config")
        {
            ApplyConfigString(rest);
        }
        else if (verb == "click")
        {
            const auto tile = mdr::text::ToLower(rest);
            if (tile == "mdr_btnplay") SendCommand("toggle");
            else if (tile == "mdr_btnnext") SendCommand("next");
            else if (tile == "mdr_btnprev") SendCommand("prev");
        }
        else if (verb == "toggle" || verb == "play" || verb == "pause" || verb == "next" || verb == "prev")
        {
            SendCommand(verb.c_str());
        }
        else if (verb == "refresh")
        {
            g_uiDirty = true;
            g_lastUiDispatch = 0;
        }
        else if (verb == "menupause")
        {
            g_pipe.Send(mdr::json::Writer("gamepause").Bool("paused", rest == "1").Finish());
        }
    }

    // ---- frame / lifecycle ------------------------------------------------------------------------

    void OnMainLoop()
    {
        std::string line;
        for (int i = 0; i < 32 && g_pipe.PopInbound(line); ++i)
            HandleInbound(line);

        const bool connected = g_pipe.IsConnected();
        const unsigned epoch = g_pipe.Epoch();
        if (connected && epoch != g_seenEpoch)
        {
            // (re)connected: bring the BridgeHost up to date
            g_seenEpoch = epoch;
            if (!g_configJson.empty()) g_pipe.Send(g_configJson);
            SendStation();
            g_pipe.Send(mdr::json::Writer("cmd").Int("id", g_nextCmdId++).Str("action", "refresh").Finish());
        }
        // MCM / Pip-Boy changes arrive as several MDR:Set events in one frame: send them together
        if (g_configDirty && GetTickCount64() - g_configChangedTick > 50)
            FlushConfig();

        // Self-check: PushConfig.gek runs right after "init". If no value ever arrived, the UDF did not
        // compile (e.g. edited by hand) — say so instead of silently ignoring MCM.
        if (g_initTick && !g_configWarned && g_config.empty() && GetTickCount64() - g_initTick > 5000)
        {
            g_configWarned = true;
            mdr::log::Info("WARNING: no MCM settings received from PushConfig.gek. Check nvse.log for "
                           "'MojaveDesktopRadio\\PushConfig.gek' compile errors. Defaults are used.");
            Notify("Mojave Desktop Radio: MCM settings not loaded (see MojaveDesktopRadio.log)");
        }

        if (connected != g_wasConnected)
        {
            g_wasConnected = connected;
            g_uiDirty = true;
        }

        if (g_gameLoaded && !g_loading && g_stationRefId)
        {
            const bool active = ReadPipboyRadioRefId(g_cfg.pipboyRadioAddress) == g_stationRefId;
            if (active != g_stationActive)
            {
                g_stationActive = active;
                mdr::log::Info("station %s", active ? "ON" : "OFF");
                SendStation();
                g_uiDirty = true;
                const bool launching = g_launchTick && GetTickCount64() - g_launchTick < 8000;
                if (active && !connected && !launching) Notify(g_cfg.msgBridgeOffline);
            }
        }

        if (g_gameLoaded && !connected && g_cfg.autoLaunch && g_firstLoadTick && g_launchAttempts < kMaxLaunchAttempts &&
            GetTickCount64() - g_firstLoadTick > g_cfg.launchDelayMs && GetTickCount64() >= g_nextLaunchTick)
        {
            TryLaunchBridge();
        }

        if (g_uiDirty && g_gameLoaded && GetTickCount64() - g_lastUiDispatch > 100)
            DispatchUi();
    }

    void LeaveGame(const char* why)
    {
        if (g_stationActive)
        {
            g_stationActive = false;
            SendStation();
        }
        g_gameLoaded = false;
        mdr::log::Info("%s", why);
    }

    void OnMessage(NVSEMessagingInterface::Message* msg)
    {
        switch (msg->type)
        {
        case NVSEMessagingInterface::kMessage_MainGameLoop:
            OnMainLoop();
            break;
        case NVSEMessagingInterface::kMessage_PreLoadGame:
            g_loading = true; // keep the station as-is across the load; re-evaluated afterwards
            break;
        case NVSEMessagingInterface::kMessage_PostLoadGame:
            g_loading = false;
            break;
        case NVSEMessagingInterface::kMessage_ExitToMainMenu:
            LeaveGame("exit to main menu");
            break;
        case NVSEMessagingInterface::kMessage_ExitGame:
        case NVSEMessagingInterface::kMessage_ExitGame_Console:
            LeaveGame("game exiting");
            g_pipe.Stop(400); // sends "bye"; BridgeHost restores audio routing
            mdr::log::Close();
            break;
        default:
            break;
        }
    }

    bool RegisterEvents()
    {
        // MDR:State (int active, int playing, signal, status, artist, track, fx, playLabel, nextLabel, prevLabel, fxLabel)
        static EM::ParamType stateParams[] = {
            EM::eParamType_Int, EM::eParamType_Int, EM::eParamType_String, EM::eParamType_String,
            EM::eParamType_String, EM::eParamType_String, EM::eParamType_String, EM::eParamType_String,
            EM::eParamType_String, EM::eParamType_String, EM::eParamType_String};
        static EM::ParamType stringParam[] = {EM::eParamType_String};
        static EM::ParamType setParams[] = {EM::eParamType_String, EM::eParamType_Float};

        bool ok = g_events->RegisterEvent("MDR:State", 11, stateParams, EM::kFlags_None);
        ok &= g_events->RegisterEvent("MDR:Notify", 1, stringParam, EM::kFlags_None);
        ok &= g_events->RegisterEvent("MDR:Cmd", 1, stringParam, EM::kFlag_AllowScriptDispatch);
        ok &= g_events->SetNativeEventHandler("MDR:Cmd", OnScriptCommand);
        ok &= g_events->RegisterEvent("MDR:Set", 2, setParams, EM::kFlag_AllowScriptDispatch);
        ok &= g_events->SetNativeEventHandler("MDR:Set", OnScriptSet);
        return ok;
    }

    std::string PluginDirectory()
    {
        HMODULE self = nullptr;
        GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCSTR>(&PluginDirectory), &self);
        char path[MAX_PATH] = {};
        GetModuleFileNameA(self, path, MAX_PATH);
        std::string p(path);

        wchar_t wpath[MAX_PATH] = {};
        GetModuleFileNameW(self, wpath, MAX_PATH);
        std::wstring w(wpath);
        g_pluginDirW = w.substr(0, w.find_last_of(L"\\/") + 1);

        return p.substr(0, p.find_last_of("\\/") + 1);
    }
}

extern "C" {

__declspec(dllexport) bool NVSEPlugin_Query(const NVSEInterface* nvse, PluginInfo* info)
{
    info->infoVersion = PluginInfo::kInfoVersion;
    info->name = "MojaveDesktopRadio";
    info->version = kPluginVersion;

    if (nvse->isEditor) return false; // runtime only
    if (nvse->nvseVersion < MIN_NVSE_VERSION) return false;
    if (nvse->runtimeVersion != RUNTIME_VERSION_1_4_0_525) return false; // address of the Pip-Boy radio is version specific
    if (nvse->isNogore) return false;
    return true;
}

__declspec(dllexport) bool NVSEPlugin_Load(const NVSEInterface* nvse)
{
    g_handle = nvse->GetPluginHandle();
    g_gameDir = nvse->GetRuntimeDirectory ? nvse->GetRuntimeDirectory() : "";

    g_cfg.Load(PluginDirectory() + "MojaveDesktopRadio.ini");
    mdr::log::Init(g_gameDir, g_cfg.debugLog);
    mdr::log::Info("Mojave Desktop Radio %s (xNVSE %08X), text code page %u", kPluginVersionString, nvse->nvseVersion, g_cfg.codePage);

    g_messaging = static_cast<NVSEMessagingInterface*>(nvse->QueryInterface(kInterface_Messaging));
    g_events = static_cast<EM*>(nvse->QueryInterface(kInterface_EventManager));
    if (!g_messaging || !g_events)
    {
        mdr::log::Info("required xNVSE interfaces are missing; plugin disabled");
        return false;
    }

    if (!RegisterEvents())
        mdr::log::Info("warning: event registration reported a problem (already registered?)");

    g_messaging->RegisterListener(g_handle, "NVSE", OnMessage);

    const std::wstring pipe(g_cfg.pipeName.begin(), g_cfg.pipeName.end());
    const std::string hello = mdr::json::Writer("hello")
                                  .Str("client", "MDR-NVSE")
                                  .Str("version", kPluginVersionString)
                                  .Int("gamePid", static_cast<long long>(GetCurrentProcessId()))
                                  .Finish();
    g_pipe.Start(pipe, hello);
    return true;
}

} // extern "C"
