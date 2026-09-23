# chatter-agent

**English** | [日本語](./README-ja.md)

**Have a VRM character read Claude Code's messages aloud, in real time.**

It takes the message text straight from Claude Code's `MessageDisplay` hook; the server formats it and synthesizes speech. The display-side apps (a macOS desktop resident app / Android XR glasses) receive it, play it, and reflect it in the VRM character's facial expressions, motion, and lip sync.

**Claude Code only, for now.** Speech is captured through Claude Code's `MessageDisplay` hook, so a tool without an equivalent hook gives it nothing to capture. Widening this is on the roadmap.

## How it works

It's a server / client setup. **Speech synthesis happens on the server side, and the client does not have a speech synthesis engine.** This is because Android XR glasses can't host a synthesis engine (AivisSpeech).

```
Claude Code
  │ hook (MessageDisplay / PreToolUse / Notification)
  ▼
┌────────────────────── server ───────────────────────┐
│ plugin                just drops the payload        │
│   ▼                                                 │
│ chatter-agent-speak   format, split into sentences, │
│   ▼                   classify emotion, assign seq  │
│ delivery queue                                      │
│   ▼                                                 │
│ chatter-agent-server                                │
│   ├─ WebSocket    deliver the speech text           │
│   ├─ GET /audio/  synthesize when fetched           │
│   └─ /v1/*        read/write settings               │
└─────────────────────────────────────────────────────┘
  ▲ ack           │ text / audio
  │               ▼
┌────────────────────── client ───────────────────────┐
│ chatter-agent-player   CLI. plays the sound         │
│ chatter-mascot         Unity. renders the VRM       │
└─────────────────────────────────────────────────────┘
```

| | Responsibilities |
|---|---|
| **server** | receiving the messages, formatting text (Markdown removal, sentence splitting, emotion classification, AI summarization), managing and delivering the speech queue, speech synthesis (including waking the synthesis engine up), holding settings |
| **client** | receiving speech, fetching and playing audio, notifying that playback finished (ack), VRM rendering / facial expressions / motion / lip sync, (desktop version only) window / staying resident / settings UI |

The speech contract is in [`docs/protocol.md`](./docs/protocol.md). If you're writing a client, that's all you need.

## Layout

| Directory | Contents |
|---|---|
| `plugin/` | The Claude Code plugin. A bash hook just drops the payload |
| `core/` | The server and CLIs (TypeScript / Node). `src/server/` `src/cli/` `src/player/` |
| `chatter-mascot/` | The display-side app (Unity + UniVRM). macOS and Android XR from a single project |
| `docs/` | The basic design, file layout, and commands. What was learned along the way is in `docs/knowledge/` |

## Current state

There are four deliverables.

| | State |
|---|---|
| **Server** (`chatter-agent-server`) | Works |
| **CLI player** (`chatter-agent-player`) | Works. Also doubles as the reference implementation of the protocol |
| **macOS client** (`chatter-mascot`) | Works. Sits resident in a transparent window, and the VRM reads speech aloud with facial expressions, motion, and lip sync. Settings can be changed from the app |
| **Android XR client** (same Unity project. Targets XREAL Aura) | Emulator only. Stands in space in OpenXR's Full Space and connects to the server over LAN. **Not yet verified on real hardware** |

## Building

**Run every command from the repository root.**

### Prerequisites

- **Node 24.11 or later** (pinned to 24.19.0 in `mise.toml`)
- **Unity 6000.3.14f1** — requires the macOS / Android build support modules
- **Unity CLI** (`unity` command) — `chatter-mascot/scripts/*.sh` run the Editor through it
- **Xcode command line tools** — used to build the native plugin for the macOS resident app (`xcode-select --install`)
- **Android SDK platform-tools** (`adb`) — when installing the Android version onto a device

Unity project setup steps are in [`docs/mascot.md`](./docs/mascot.md).

### Server and CLIs

```bash
cd core
npm install
npm run build
```

This produces `plugin/bin/chatter-agent-speak.mjs` (the CLI bundled with the plugin) and the server / player under `core/dist/`.

### macOS client

```bash
cd chatter-mascot
./scripts/build.sh           # → Build/ChatterMascot.app
```

`build.sh` builds the native plugin (Objective-C) for the resident app first. **Right after cloning,
finish the setup in [`docs/mascot.md`](./docs/mascot.md) before opening Unity.**

### Android XR client

```bash
cd chatter-mascot
./scripts/build-android.sh   # → Build/ChatterMascot.apk
```

## Running

**Run every command from the repository root.**

### Prerequisites

- **Claude Code**
- **[AivisSpeech](https://aivis-project.com/)** — **just having it installed is enough.** If the synthesis engine isn't running, the server wakes it up, and stopping the server takes it down too. You only need to start it by hand to connect to an engine on a different host, or **to add a speaker** (adding a voice model needs the GUI). To turn the auto-start off entirely, set `CHATTER_AGENT_TTS_SPAWN=0`
- **macOS** — the display apps target macOS and Android XR, the playback command defaults to `afplay`, and the engine is auto-discovered only at macOS paths. On Linux / Windows, point the CLI player at a playback command with `CHATTER_AGENT_PLAYER_COMMAND`

### Installing the Claude Code plugin

```bash
claude plugin marketplace add ./
claude plugin install chatter-agent@chatter-agent
```

**Restart your session.** You can check whether it took by looking at the speech record.

```bash
tail -f "${XDG_CONFIG_HOME:-$HOME/.config}/chatter-agent/speech.jsonl"
```

To silence it, start Claude Code with `CHATTER_AGENT_DISABLE=1` set.

### Server

```bash
cd core && npm run start:server
```

### Client

How the server and client pair up depends on the case. See
[`docs/mascot.md`](./docs/mascot.md) ("接続" / Connection) for the details.

| Case | Server | Client |
|---|---|---|
| A. Mac only | `cd core && npm run start:server` | macOS app / CLI player |
| B. Emulator or a USB-connected device, against the Mac's server | Start it as-is (listens on loopback only by default) | `./scripts/run-android.sh` (goes over `adb reverse`, so it needs neither a token nor exposing the server to the LAN. Keep the device's `connection` empty — `./scripts/configure-android.sh --clear`) |
| C. Over the LAN (a Wi-Fi device) | `CHATTER_AGENT_HOST=0.0.0.0 npm run start:server` | `./scripts/configure-android.sh` → `./scripts/run-android.sh` |
| D. Running Mac and Android at once | A second server on its own runtime root and port | Case B or C, pointed at that port |

```bash
# CLI player (audio only. Sound plays without waiting on Unity)
cd core && npm run start:player
```

```bash
# macOS
open chatter-mascot/Build/ChatterMascot.app
```

```bash
# Android (case B: emulator or a USB-connected device, against the Mac's server)
cd chatter-mascot
./scripts/run-android.sh
```

```bash
# Android (case C: over the LAN)
cd chatter-mascot
./scripts/configure-android.sh          # assembles the LAN IP automatically
./scripts/run-android.sh                # installs, launches, and streams logcat
```

## Models and motion

**The main settings can be changed from the macOS client.** Right-click the character, or use the menu bar icon, to open the settings panel — model, size, volume, speaking speed, voice style, motion, summarization, shortcuts.

Settings and assets live under `~/.config/chatter-agent/` (or under `XDG_CONFIG_HOME` if you've set it).

```
~/.config/chatter-agent/
├── config.json               server settings
├── emotion-keywords.json     emotion-classification keywords (delete to restore defaults)
├── mascot/settings.json      client settings
├── mascot/window.json        window position and size
├── models/mascot.vrm         the model chosen in the settings panel
└── animations/               motion (placed by hand)
```

**Only one CC0 model and one idle-loop motion are bundled.** Emotion motions aren't bundled because there's no redistributable asset for them — **place `.vrma` files by hand.**

| Where to put it | What plays |
|---|---|
| `animations/*.vrma` (directly under it) | replaces the idle loop (in place of the bundled one) |
| `animations/idle/` | little extras while idle (one every 30–60 seconds) |
| `animations/happy/` `angry/` `sad/` `relaxed/` `surprised/` | plays once for speech with that emotion → returns to idle |
| `animations/walk/` | the walking motion for wandering in XR (looped; unused on macOS) |

The model can be swapped from the settings panel, but **there's no UI for motion.** Create the directory, drop a `.vrma` file in, and it's picked up on the next launch. There's no directory for `neutral` (the default behavior is not to play an emotion motion).

**Android XR uses the same directories** (under `Android/data/tech.sukima.chattermascot/files/` on the
device), but **you don't need `adb push`.** Once the connection target and token are configured
(`configure-android.sh`), it fetches automatically from the server's `GET /v1/assets` on every launch
(default `auto`; takes effect from the next launch). Manually placed files still work too, and take
priority over synced ones. **The two files directly under those directories are still fixed names: it
reads only `models/mascot.vrm` and `animations/idle.vrma`.** The free-form name in the table above
(`animations/*.vrma`) has no effect there. **The per-category directories (`animations/happy/` and so
on) do take free-form names, whether synced or manually placed.** See
[`docs/mascot.md`](./docs/mascot.md) ("モデルとモーションを入れる").

## Development

```bash
cd core
npm run typecheck
npm run lint
npm run format
npm run test:run

npm run verify:phase-a   # hook → record + delivery queue
npm run verify:phase-b   # delivery queue → WebSocket
npm run verify:tts       # synthesis and GET /audio/
npm run verify:player    # WebSocket → fetch audio → play → ack
npm run verify:assets    # manifest → GET /v1/assets/ → resume via Range
```

`verify:tts` and `verify:player` swap in stubs for the synthesis engine and the playback command, so you need neither AivisSpeech nor an audio device.

```bash
cd chatter-mascot
./scripts/test.sh        # EditMode tests
```

| Document | When to read it |
|---|---|
| [`CLAUDE.md`](./CLAUDE.md) | the overall design, and the invariants you must not break |
| [`docs/protocol.md`](./docs/protocol.md) | the speech contract. When writing a client |
| [`docs/core.md`](./docs/core.md) | when touching `core/` |
| [`docs/plugin.md`](./docs/plugin.md) | when touching `plugin/` |
| [`docs/mascot.md`](./docs/mascot.md) | when touching the display-side app |
| [`docs/origin.md`](./docs/origin.md) | when touching code that comes from cc-mascot |
| [`docs/knowledge/`](./docs/knowledge) | what was learned along the way, why, and measured values |

## Roadmap

- Multi-language support — make the TTS swappable / drop the dictionary-based emotion classification
- Support for coding agents other than Claude
- Support for moving around in XR space to some degree, and spatial anchor support
- CI setup and distribution to various app stores
- Galaxy XR support
- Making it displayable in the Android XR Home Scene as well
- Making it work on Android glasses (INAIR Pod / Viture Neckband, etc.)

## License

[Apache-2.0](./LICENSE). A derivative of [cc-mascot](https://github.com/kazakago/cc-mascot) (Apache-2.0, Copyright 2026 kazakago). Attribution is in [`NOTICE`](./NOTICE), and porting details are in [`docs/origin.md`](./docs/origin.md).
