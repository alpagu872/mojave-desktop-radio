# MDR protocols (v1)

## Game scripts ↔ NVSE plugin (xNVSE events, no opcodes)

The plugin registers no script commands, so it needs no opcode range and cannot collide with other plugins.

| Event | Direction | Arguments |
|---|---|---|
| `MDR:Cmd` | script → plugin | 1 string: `init <modIndex>`, `click <TileName>`, `toggle`, `play`, `pause`, `next`, `prev`, `refresh`, `menupause 0/1`, (legacy) `config k=v;…` |
| `MDR:Set` | script → plugin | string key, number value: `fx preset volume static saturation custom lowpass highpass width fade tuning autopause autoplay pausemenu trackmsg player` (MCM units: 0/1 toggles, preset 0–3, percentages 0–100, Hz, ms, player 0–5 = spotify foobar2000 musicbee aimp wacup custom — same order in MCM, `main.cpp` and `PlayerProfiles.McmOrder`, checked by `lint_scripts.py`). Values received in one frame are sent to the BridgeHost as one `config` message. |
| `MDR:State` | plugin → script | int active, int playing, int revision, 9 strings: signal, status, artist, track, fx, playLabel, nextLabel, prevLabel, fxLabel — in the game's code page. Sent on every change and 4x per second (never during a load); `OnState.gek` writes the panel only when its `_MDR_Rev` differs from `revision`. |
| `MDR:Notify` | plugin → script | 1 string corner message |
| `MDR_OnConfigChanged`, `MDR_OnAssignKeys` | MCM → script | user events dispatched by MCM Extender |

Rules for scripts (enforced by `tools/lint_scripts.py`): one value per `DispatchEventAlt` line, no string
building with `+ (sv_Construct …)`, lines ≤ 300 characters.

## NVSE plugin ↔ BridgeHost (named pipe)

* Transport: `\\.\pipe\MojaveDesktopRadio` (name configurable on both sides). The BridgeHost is the server.
* Security: `PipeOptions.CurrentUserOnly` — only the same Windows user can connect; remote clients are
  rejected. No TCP/UDP port is opened.
* Framing: one UTF-8 JSON object per line (`\n`), max 16 KB per line.
* Every message has `"v": 1` and `"type"`. Unknown fields are ignored. A different `v` is answered with
  `unsupported_version`, an unknown type with `unknown_type`.
* Heartbeat: both sides send `ping` about every 2 s and answer `pong`. The client drops the connection
  after 8 s of silence, the server after 10 s. The client reconnects automatically and then sends
  `hello`, the last `config`, the current `station` and `cmd refresh`.

### Client → server

| type | fields | meaning |
|---|---|---|
| `hello` | `client` (`"MDR-NVSE"` = the game), `version`, `gamePid` | first message |
| `station` | `active` (bool) | Pip-Boy tuned to / away from the station |
| `cmd` | `id`, `action`: `play` `pause` `toggle` `next` `prev` `refresh` `desktop_fx_on` `desktop_fx_off` | answered with `ack` or `error` |
| `config` | `player` (`spotify` `foobar2000` `musicbee` `aimp` `wacup` `custom`; absent = keep the BridgeHost's setting), `fxEnabled`, `preset` (`Clean`/`Mojave`/`Wasteland`/`Damaged`), `volume` (0–1.5), `static` (0–1), `saturation` (0–1), `customTone`, `lowPassHz`, `highPassHz`, `stereoWidth` (0–1), `fadeMs`, `tuningFx`, `autoPause`, `autoPlay`, `pauseMenu` (0 keep, 1 pause, 2 lower) | settings |
| `gamepause` | `paused` | game pause menu |
| `status` | – | request a `state` |
| `ping` / `pong` | – | heartbeat |
| `bye` | – | game is closing (the BridgeHost restores routing) |

### Server → client

| type | fields |
|---|---|
| `welcome` | `server`, `serverVersion`, `protocol` |
| `state` | `seq`, `player` (`not_running` `no_session` `stopped` `paused` `playing` `changing`), `playerKey`, `playerName` (display name of the controlled player), `playing`, `artist`, `title`, `album`, `canNext`, `canPrevious`, `fx` (effective: `Clean` `MojaveRadio` `WastelandRadio` `DamagedRadio`), `fxRequested`, `fxEnabled`, `fxActive`, `route` (`off` `pending` `routed` `manual` `unavailable`), `routeDetail`, `stationActive` |
| `ack` | `id`, `ok`, `result` |
| `error` | `id` (if any), `code` (`player_not_running` `player_no_session` (0.2.1 and older: `spotify_…`) `timeout` `rejected` `unknown_action` `unsupported_version` `bad_message` `line_too_long` `unknown_type`), `message` |

`state` is sent only when something changed (or on request).

```json
{"v":1,"type":"cmd","id":12,"action":"next"}
{"v":1,"type":"state","seq":41,"player":"playing","playerKey":"spotify","playerName":"Spotify","playing":true,"artist":"Example Artist","title":"Example Track","album":"…","canNext":true,"canPrevious":true,"fx":"MojaveRadio","fxRequested":"MojaveRadio","fxEnabled":true,"fxActive":true,"route":"routed","routeDetail":"CABLE Input (VB-Audio Virtual Cable)","stationActive":true}
```
