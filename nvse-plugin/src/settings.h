#pragma once
#include <string>

namespace mdr
{
    // Data\NVSE\Plugins\MojaveDesktopRadio.ini — technical settings and Pip-Boy texts.
    // (Gameplay/FX options live in Data\Config\MojaveDesktopRadio.ini and are edited through MCM.)
    struct Settings
    {
        std::string pipeName = "MojaveDesktopRadio";
        unsigned codePage = 0; // 0 = Windows ANSI code page (GetACP)
        bool debugLog = false;
        unsigned stationLocalId = 0x000802; // REFR "MDR_DesktopRadioStationRef" inside MojaveDesktopRadio.esp
        unsigned pipboyRadioAddress = 0x011DD42C; // RadioEntry* (1.4.0.525); [Debug] override only for the offline harness

        bool autoLaunch = true;
        std::string bridgePath; // empty = %LOCALAPPDATA%\MojaveDesktopRadio\app\MojaveDesktopRadio.exe
        unsigned launchDelayMs = 1500;
        std::string bridgeExtraArgs; // advanced: e.g. "--verbose"

        unsigned maxTitleChars = 40;

        // Pip-Boy texts (already in the game code page; edit the INI to translate)
        std::string signalConnected = "SIGNAL: CONNECTED";
        std::string signalLost = "SIGNAL LOST";
        std::string signalBridgeOffline = "SIGNAL LOST - BRIDGE OFFLINE";
        // %s = the player's name (SPOTIFY, FOOBAR2000, ... - MCM > Desktop Player)
        std::string playerNotFound = "STATUS: %s NOT RUNNING";
        std::string playerNoSession = "STATUS: PRESS PLAY IN %s ONCE";
        std::string statusPlaying = "STATUS: PLAYING";
        std::string statusPaused = "STATUS: PLAYBACK PAUSED";
        std::string statusStopped = "STATUS: STOPPED";
        std::string artistPrefix = "ARTIST: ";
        std::string trackPrefix = "TRACK : ";
        std::string fxPrefix = "FX MODE: ";
        std::string fxClean = "CLEAN";
        std::string fxMojave = "MOJAVE RADIO";
        std::string fxWasteland = "WASTELAND RADIO";
        std::string fxDamaged = "DAMAGED RADIO";
        std::string fxOff = "OFF";
        std::string fxPendingSuffix = " (TUNING...)";
        std::string fxUnavailableSuffix = " (NO ROUTE - CLEAN)";
        std::string labelPlay = "> PLAY";
        std::string labelPause = "> PAUSE";
        std::string labelNext = "> NEXT TRACK";
        std::string labelPrev = "> PREVIOUS TRACK";
        std::string labelFx = "> CHANGE FX MODE";
        std::string nowPlayingFormat = "Now Playing: %s"; // %s = "Artist - Title"
        std::string msgBridgeOffline = "Mojave Desktop Radio: BridgeHost is not running";
        std::string msgPlayerNotFound = "Mojave Desktop Radio: %s is not running";

        void Load(const std::string& iniPath);
    };
}
