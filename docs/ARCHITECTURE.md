# Architecture and design decisions

```
 Fallout: New Vegas (32-bit)                                 Windows desktop (64-bit)
 ┌──────────────────────────────────────────┐   named pipe   ┌──────────────────────────────────────┐
 │ MojaveDesktopRadio.esp  (station)        │  JSON lines    │ BridgeHost (tray app, .NET 8)        │
 │ MojaveDesktopRadio.dll  (xNVSE plugin) ◄─┼────────────────┼─► engine thread                      │
 │   reads Pip-Boy radio pointer per frame  │  current user  │   GSMTC: Spotify session only        │
 │   pipe client on its own thread          │  only          │   per-app routing + restore          │
 │ scripts / UDFs (JIP script runner)       │                │   FX pipeline: loopback → DSP → out  │
 │ Pip-Boy panel XML (UIO), MCM JSON        │                └──────────────────────────────────────┘
 └──────────────────────────────────────────┘
```

## Game side

| Decision | Reason / evidence |
|---|---|
| A 3-record ESP (TACT + private interior CELL + persistent REFR) | The Pip-Boy station list is built from talking activators with the *Radio Station* record flag (`0x00020000`) whose persistent reference broadcasts. Our records copy the vanilla *Mojave Music Radio* (`vCountryRadio`, REFR in `Vault11a`, `XRDO type 1 = Everywhere`) byte-for-byte in layout — checked by `tools/inspect_plugin.py` against `FalloutNV.esm`. No dialogue/quest, so the game plays nothing on it. Nothing is overridden → save-safe. |
| No script commands | Registering commands needs an assigned opcode range; scripts and plugin talk through xNVSE events (`RegisterEvent` + `kFlag_AllowScriptDispatch`, `SetNativeEventHandler`, `DispatchEvent`). |
| Station detection in C++ | One guarded pointer read per frame: `*(RadioEntry**)0x011DD42C → radioRef → refID (+0x0C)` (address from JIP LN, 1.4.0.525 only; the plugin refuses other runtimes). Works even if the UI scripts fail. |
| Pip-Boy panel via UIO | Inserted into `MapMenu/GLOW_BRANCH/MM_MainRect`, visible only on the Radio tab (`MM_Tabline._CurrentTab == 4`) while the station is tuned. No VUI+/vanilla file is replaced. |
| Real buttons | JIP `SetOnMenuClickEventHandler` with a tile-path prefix (same technique as PipBoy Casino). The engine calls `HandleClick(-1, tile)` for id-less tiles, MapMenu ignores it, JIP's hook forwards it to `OnClick.gek`. |
| MCM → AuxVars → `PushConfig.gek` → `MDR:Set` | MCM Extender writes the INI through ShowOff's *cached* INI functions (saved on pause-menu close/exit), so the plugin cannot read changes from disk. |
| Text code page | Spotify gives UTF-16; the game draws 8-bit text. Default = Windows ANSI code page (1254 for the Turkish translation, 1252 Western). |
| Non-blocking | The pipe client runs on its own thread with overlapped I/O; the main thread only touches two small queues. BridgeHost offline, crashed or slow → the game is not affected. |

## BridgeHost

| Decision | Reason |
|---|---|
| Windows GSMTC instead of the Spotify Web API | play/pause/next/previous and title/artist are all available locally; no OAuth, no Premium requirement, no network traffic. Only sessions whose `SourceAppUserModelId` contains `Spotify` are touched — never "the current session", so other players are never controlled. If Spotify is open but has no session yet, a play command is sent to Spotify's own window (`WM_APPCOMMAND`), never as a global media key. |
| One actor thread (MTA) | IPC, polling, device notifications and tray actions are posted to it: no races, no COM apartment issues. Endpoint names are cached (reading them costs ~1.5 s per pass on a PC with 9 devices). |
| Named pipe, `CurrentUserOnly` | local only, no ports |
| Launched by the plugin with `--game-pid`, exits with the game | one archive to install; MO2 is unlocked a few seconds after quitting |

## Radio FX

* To process Spotify's sound without hearing the original too, Spotify must play to a device nobody listens
  to. Windows cannot capture an application that is muted (muting happens before capture).
* While the station is tuned (and FX is on), the BridgeHost sets Spotify's per-app output device to the
  inaudible device (VB-CABLE / Steam Streaming Speakers), captures that device with WASAPI loopback,
  processes it and renders it to the normal output. Fallout's audio never passes through it.
* Per-app routing uses Windows' internal `IAudioPolicyConfigFactory` (documented nowhere; layout as in
  EarTrumpet). Every change is written to a marker file first and undone when the station is left, on
  disconnect, on exit, and on the next start after a crash. If the API fails, the engine stays "Clean".
  Chromium sub-processes answer `E_INVALIDARG`; one accepted process is enough (the setting is per app).
* DSP chain (all real-time safe): width → wow (modulated delay) → fading/dropouts → hiss/crackle → 24 dB HP →
  24 dB LP (drifting) → speaker peak EQ → compressor → saturation → tuning sweep → soft limiter. Clean is a
  bit-exact bypass. All noises are generated; no sample files.
* Latency: capture ~10 ms + ring buffer ~30 ms + WASAPI output 60 ms. Clock drift between devices is handled
  by skipping when the buffer exceeds 160 ms.

## Known limits

* Gamepads cannot navigate the panel (hotkeys in MCM instead).
* Spotify must have played once since it started before it has a Windows media session.
* Steam Streaming Speakers as FX device: Steam Remote Play would stream Spotify while the station is on.
* DarNified UI and TTW are untested.
