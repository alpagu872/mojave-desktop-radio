#include "settings.h"

#include <windows.h>

#include <cctype>
#include <cstdlib>

namespace mdr
{
    namespace
    {
        std::string ReadString(const std::string& ini, const char* section, const char* key, const std::string& def)
        {
            // GetPrivateProfileString trims the *default* value, so detect "missing" with a sentinel
            // and keep our default (with its trailing spaces, e.g. "ARTIST: ") untouched.
            static const char kMissing[] = "\x01mdr-missing\x01";
            char buf[1024];
            DWORD n = GetPrivateProfileStringA(section, key, kMissing, buf, sizeof(buf), ini.c_str());
            std::string v(buf, n);
            if (v == kMissing) return def;
            // allow quoting to keep leading/trailing spaces: Key="ARTIST: "
            if (v.size() >= 2 && v.front() == '"' && v.back() == '"') v = v.substr(1, v.size() - 2);
            return v;
        }

        unsigned ReadUInt(const std::string& ini, const char* section, const char* key, unsigned def)
        {
            std::string s = ReadString(ini, section, key, "");
            if (s.empty()) return def;
            char* end = nullptr;
            unsigned long v = std::strtoul(s.c_str(), &end, 0); // accepts 0x...
            return end == s.c_str() ? def : static_cast<unsigned>(v);
        }

        bool ReadBool(const std::string& ini, const char* section, const char* key, bool def)
        {
            return ReadUInt(ini, section, key, def ? 1u : 0u) != 0;
        }
    }

    void Settings::Load(const std::string& ini)
    {
        pipeName = ReadString(ini, "General", "sPipeName", pipeName);
        codePage = ReadUInt(ini, "General", "iCodePage", codePage);
        debugLog = ReadBool(ini, "General", "bDebugLog", debugLog);
        stationLocalId = ReadUInt(ini, "General", "iStationRefLocalId", stationLocalId) & 0xFFFFFF;
        maxTitleChars = ReadUInt(ini, "General", "iMaxTextChars", maxTitleChars);
        pipboyRadioAddress = ReadUInt(ini, "Debug", "iPipboyRadioAddress", pipboyRadioAddress);

        autoLaunch = ReadBool(ini, "Bridge", "bAutoLaunch", autoLaunch);
        bridgePath = ReadString(ini, "Bridge", "sPath", bridgePath);
        launchDelayMs = ReadUInt(ini, "Bridge", "iLaunchDelayMs", launchDelayMs);
        bridgeExtraArgs = ReadString(ini, "Bridge", "sExtraArgs", bridgeExtraArgs);
        // the pipe name is passed on the command line: keep it to a safe character set
        if (pipeName.empty() || pipeName.size() > 64) pipeName = "MojaveDesktopRadio";
        for (char c : pipeName)
            if (!(isalnum(static_cast<unsigned char>(c)) || c == '-' || c == '_' || c == '.')) { pipeName = "MojaveDesktopRadio"; break; }

        signalConnected = ReadString(ini, "Text", "sSignalConnected", signalConnected);
        signalLost = ReadString(ini, "Text", "sSignalLost", signalLost);
        signalBridgeOffline = ReadString(ini, "Text", "sSignalBridgeOffline", signalBridgeOffline);
        playerNotFound = ReadString(ini, "Text", "sPlayerNotFound", playerNotFound);
        playerNoSession = ReadString(ini, "Text", "sPlayerNoSession", playerNoSession);
        statusPlaying = ReadString(ini, "Text", "sStatusPlaying", statusPlaying);
        statusPaused = ReadString(ini, "Text", "sStatusPaused", statusPaused);
        statusStopped = ReadString(ini, "Text", "sStatusStopped", statusStopped);
        artistPrefix = ReadString(ini, "Text", "sArtistPrefix", artistPrefix);
        trackPrefix = ReadString(ini, "Text", "sTrackPrefix", trackPrefix);
        fxPrefix = ReadString(ini, "Text", "sFxPrefix", fxPrefix);
        fxClean = ReadString(ini, "Text", "sFxClean", fxClean);
        fxMojave = ReadString(ini, "Text", "sFxMojave", fxMojave);
        fxWasteland = ReadString(ini, "Text", "sFxWasteland", fxWasteland);
        fxDamaged = ReadString(ini, "Text", "sFxDamaged", fxDamaged);
        fxOff = ReadString(ini, "Text", "sFxOff", fxOff);
        fxPendingSuffix = ReadString(ini, "Text", "sFxPendingSuffix", fxPendingSuffix);
        fxUnavailableSuffix = ReadString(ini, "Text", "sFxUnavailableSuffix", fxUnavailableSuffix);
        labelPlay = ReadString(ini, "Text", "sLabelPlay", labelPlay);
        labelPause = ReadString(ini, "Text", "sLabelPause", labelPause);
        labelNext = ReadString(ini, "Text", "sLabelNext", labelNext);
        labelPrev = ReadString(ini, "Text", "sLabelPrevious", labelPrev);
        labelFx = ReadString(ini, "Text", "sLabelFx", labelFx);
        nowPlayingFormat = ReadString(ini, "Text", "sNowPlayingFormat", nowPlayingFormat);
        msgBridgeOffline = ReadString(ini, "Text", "sMsgBridgeOffline", msgBridgeOffline);
        msgPlayerNotFound = ReadString(ini, "Text", "sMsgPlayerNotFound", msgPlayerNotFound);

        // 0 = automatic: the Windows ANSI code page (1254 on Turkish Windows, 1252 Western, 1251 Cyrillic ...),
        // which is what localized FNV versions/patches use for their text.
        if (codePage == 0) codePage = GetACP();
        if (maxTitleChars < 8) maxTitleChars = 8;
        if (nowPlayingFormat.find("%s") == std::string::npos) nowPlayingFormat += " %s";
    }
}
