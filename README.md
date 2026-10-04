# chatter-agent

**English** | [日本語](./README-ja.md)

**Have a VRM character read Claude Code's messages aloud, in real time.**

It takes the message text straight from Claude Code's `MessageDisplay` hook; the server formats it and synthesizes speech. The display-side apps (a macOS desktop resident app / Android XR glasses and headsets) receive it, play it, and reflect it in the VRM character's facial expressions, motion, and lip sync.

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
| **client** | receiving speech, fetching and playing audio, notifying that playback finished (ack), VRM rendering / facial expressions / motion / lip sync, (Android XR only) the in-space settings panel, (desktop version only) window / staying resident. The desktop settings UI lives in ChatterAgent |

The speech contract is in [`docs/protocol.md`](./docs/protocol.md). If you're writing a client, that's all you need.

## Layout

| Directory | Contents |
|---|---|
| `plugin/` | The Claude Code plugin. A bash hook just drops the payload |
| `core/` | The server and CLIs (TypeScript / Node). `src/server/` `src/cli/` `src/player/` |
| `chatter-mascot/` | The display-side app (Unity + UniVRM). macOS and Android XR from a single project |
| `chatter-agent-app/` | ChatterAgent (Tauri). A menu bar app that starts and stops the server, hosts the settings panel, and shows, hides, mutes, resizes the mascot and previews its motions |
| `docs/` | The basic design, file layout, and commands. What was learned along the way is in `docs/knowledge/` |

## Current state

There are four deliverables.

| | State |
|---|---|
| **Server** (`chatter-agent-server`) | Works |
| **CLI player** (`chatter-agent-player`) | Works. Also doubles as the reference implementation of the protocol |
| **macOS client** (`chatter-mascot`) | Works. Sits resident in a transparent window, and the VRM reads speech aloud with facial expressions, motion, and lip sync. Settings can be changed from the app |
| **Android XR client** (same Unity project. Targets XREAL Aura / Galaxy XR) | Emulator only. Stands in space in OpenXR's Full Space, and the room shows through the background on both glasses and headsets. Connects to the server over LAN. **Not yet verified on real hardware** |

## Building

**Run every command from the repository root.**

### Prerequisites

- **Node 24.11 or later** (pinned to 24.19.0 in `mise.toml`)
- **Rust** (pinned in `chatter-agent-app/mise.toml`) — to build ChatterAgent
- **Unity 6000.3.14f1** — requires the macOS / Android build support modules
- **Unity CLI** (`unity` command) — `chatter-mascot/scripts/*.sh` run the Editor through it
- **Git LFS** — the mascot's bundled model, motion and icons are LFS-tracked. Cloning without it builds with pointer files in their place, and if you rely on the bundled model only a Cube appears (if already cloned, run `git lfs install` then `git lfs pull`, and rebuild)
- **Android SDK platform-tools** (`adb`) — when installing the Android version onto a device

Unity project setup steps are in [`docs/mascot.md`](./docs/mascot.md).

### Server and CLIs

```bash
cd core
npm install
npm run build
```

This produces `plugin/bin/chatter-agent-speak.mjs` (the CLI bundled with the plugin) and the server / player under `core/dist/`.

### ChatterAgent (macOS)

```bash
cd chatter-agent-app
npm install
npm run build      # → build/ChatterAgent.app
```

The menu bar app that starts and stops the server and hosts the settings panel. How to use it is under "Running" → "Server".

### macOS client

```bash
cd chatter-mascot
./scripts/build.sh           # → Build/ChatterMascot.app
```

**Right after cloning, finish the setup in [`docs/mascot.md`](./docs/mascot.md) before opening Unity.**
The mascot only renders and plays: it has no settings panel, menu bar item, or shortcuts (and no Dock icon).
Right-clicking the character opens ChatterAgent's settings panel (if ChatterAgent isn't running, it is just launched).

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
- **The synthesis engine is swappable** (`ttsEngine`). The default is AivisSpeech (VOICEVOX-compatible); for languages other than Japanese, **[Kokoro-FastAPI](https://github.com/remsky/Kokoro-FastAPI)** (OpenAI-compatible) is also supported. Setup steps are in [`docs/kokoro.md`](./docs/kokoro.md)
- **macOS** — the display apps target macOS and Android XR, the playback command defaults to `afplay`, and the engine is auto-discovered only at macOS paths. On Linux / Windows, point the CLI player at a playback command with `CHATTER_AGENT_PLAYER_COMMAND`
- **Summarization (optional)** — shortens long messages before reading them aloud. The default backend is `fm` (the Apple Foundation Models CLI, macOS 27+). Where it's unavailable (pre-macOS 27, Linux, Windows), the original text is read as-is. `aiSummaryBackend` can be switched to `claude` (`claude -p`) instead
- **Emotion classification (optional)** — picks an expression for each utterance. The default is **[Ollaya](https://ollaya.dev/)** (a local decision model). Run `ollaya pull laya:multilingual` once to fetch the model; after that the server wakes it up whenever it isn't running, the same way it does for AivisSpeech. Where it's not installed or unreachable, classification falls back to the dictionary-based classifier, so speech never stops. `emotionClassifier` can also be switched to `fm` or `dictionary` (the original rule-based classifier)

### Installing the Claude Code plugin

**Straight from GitHub** (`--sparse` fetches only the plugin part):

```bash
claude plugin marketplace add schwarz9791/chatter-agent --sparse .claude-plugin plugin
claude plugin install chatter-agent@chatter-agent
```

To update, refresh the marketplace and then update the plugin. Updates arrive when the plugin's
`version` goes up.

```bash
claude plugin marketplace update chatter-agent
claude plugin update chatter-agent@chatter-agent
```

**From a clone** (for development):

```bash
claude plugin marketplace add ./
claude plugin install chatter-agent@chatter-agent
```

In this case the clone itself is what runs, so pulling and restarting your session is enough to pick up new changes.

**Restart your session.** You can check whether it took by looking at the speech record.

```bash
tail -f "${XDG_CONFIG_HOME:-$HOME/.config}/chatter-agent/speech.jsonl"
```

To silence it, start Claude Code with `CHATTER_AGENT_DISABLE=1` set.

### Server

```bash
cd core && npm run start:server
```

On macOS you can let **ChatterAgent** (a menu bar app) start, stop, and restart the server instead.
Once `core/` and ChatterAgent are built (see "Building"), open `chatter-agent-app/build/ChatterAgent.app` and choose the `core/` folder once from the menu ("Choose Core Folder…"). It then starts
the server with the same environment a login shell gives you. Starting it by hand as above keeps
working; ChatterAgent shows it as running outside and leaves it alone. Its settings panel (menu → "Settings…")
edits the server settings and the mascot settings (model, size, volume, motion, and so on). Right-clicking the mascot, or reopening ChatterAgent from Finder, brings the settings window to the front too.

"Show Mascot / Hide Mascot" and "Mute" in the menu also switch which client is connected. While the
mascot is shown, the macOS app plays the speech; while it is hidden, the CLI player plays the voice
only; when it is hidden and muted, nothing is connected (connecting both would play everything twice,
so ChatterAgent runs only one of them). The macOS app is looked up as `ChatterMascot.app` in
`/Applications`, then `~/Applications`, then `chatter-mascot/Build`. Shortcuts work too (by default
⌃⌥M toggles mute and ⌃⌥H shows or hides the mascot; only ChatterAgent registers them). Details are in
[`docs/agent.md`](./docs/agent.md).

### Client

How the server and client pair up depends on the case. See
[`docs/mascot.md`](./docs/mascot.md) ("接続" / Connection) for the details.

| Case | Server | Client |
|---|---|---|
| A. Mac only | `cd core && npm run start:server` | macOS app / CLI player |
| B. Emulator or a USB-connected device, against the Mac's server | Start it as-is (listens on loopback only by default) | `./scripts/run-android.sh` (goes over `adb reverse`, so it needs neither a token nor exposing the server to the LAN. Keep the device's `connection` empty — `./scripts/configure-android.sh --clear`) |
| C. Over the LAN (a Wi-Fi device) | `CHATTER_AGENT_HOST=0.0.0.0 npm run start:server` | XR: enter the 4-digit PIN shown by ChatterAgent's "Pair with Android…" into the device's settings panel ("Pair…"). Non-XR devices, or when mDNS doesn't reach: `./scripts/configure-android.sh` → `./scripts/run-android.sh` |
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

On Android XR, case C works without adb once the app is installed. Start the server with `0.0.0.0`, put the device and the Mac on the same Wi-Fi, then:

1. Open "Pair with Android…" from ChatterAgent's menu (a 4-digit PIN appears; valid for 5 minutes, 5 wrong tries allowed)
2. In the device's settings panel, open "Pair…", enter the PIN, and press "Pair"
3. On success the token is saved on the device and it reconnects on the spot (no restart; the device finds the Mac over mDNS)

```bash
# Android (case C: over the LAN. A non-XR device, or when mDNS doesn't reach)
cd chatter-mascot
./scripts/configure-android.sh          # the device finds the Mac on the LAN (mDNS)
./scripts/run-android.sh                # installs, launches, and streams logcat
```

## Models and motion

**The main settings can be changed from ChatterAgent's settings panel.** Right-click the character, or use ChatterAgent's menu bar icon, to open it — model, size, volume, speaking speed, voice style, motion preview, summarization, shortcuts. The mascot itself only reads the settings; it never writes them.

Settings and assets live under `~/.config/chatter-agent/` (or under `XDG_CONFIG_HOME` if you've set it).

```
~/.config/chatter-agent/
├── config.json               server settings
├── emotion-keywords.json     emotion-classification keywords (delete to restore defaults)
├── mascot/settings.json      client settings (size is `character.scale`; only ChatterAgent writes it)
├── mascot/window.json        window position
├── models/mascot.vrm         the model chosen in ChatterAgent's settings panel
└── animations/               motion (placed by hand)
```

**Only one CC0 model and one idle-loop motion are bundled.** Emotion motions aren't bundled because there's no redistributable asset for them — **place `.vrma` files by hand.**

| Where to put it | What plays |
|---|---|
| `animations/*.vrma` (directly under it) | replaces the idle loop (in place of the bundled one) |
| `animations/idle/` | little extras while idle (one every 30–60 seconds) |
| `animations/happy/` `angry/` `sad/` `relaxed/` `surprised/` | plays once for speech with that emotion → returns to idle |
| `animations/walk/` | the walking motion for wandering in XR (looped; unused on macOS) |

The model can be swapped from ChatterAgent's settings panel, and motions can be previewed in its "Motion" section, but **there's no UI for placing motions.** Create the directory, drop a `.vrma` file in, and it's picked up on the next launch. There's no directory for `neutral` (the default behavior is not to play an emotion motion).

**Android XR uses the same directories** (under `Android/data/tech.sukima.chattermascot/files/` on the
device), but **you don't need `adb push`.** Once the connection target and token are configured
(pairing, or `configure-android.sh`), it fetches automatically from the server's `GET /v1/assets` on every launch
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
| [`docs/agent.md`](./docs/agent.md) | when touching ChatterAgent (the menu bar app that looks after the server and the mascot) |
| [`docs/origin.md`](./docs/origin.md) | when touching code that comes from cc-mascot |
| [`docs/kokoro.md`](./docs/kokoro.md) | when using Kokoro-FastAPI (TTS for non-Japanese languages) |
| [`docs/knowledge/`](./docs/knowledge) | what was learned along the way, why, and measured values |

## Roadmap

- Support for coding agents other than Claude
- Spatial anchor support (keeping a repositioned placement across restarts)
- CI setup and distribution to various app stores
- Letting it run alongside Home Scene apps on Android XR
- Making it work on Android glasses (INAIR Pod / Viture Neckband, etc.)

## License

[Apache-2.0](./LICENSE). A derivative of [cc-mascot](https://github.com/kazakago/cc-mascot) (Apache-2.0, Copyright 2026 kazakago). Attribution is in [`NOTICE`](./NOTICE), and porting details are in [`docs/origin.md`](./docs/origin.md).
