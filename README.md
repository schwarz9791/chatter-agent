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
| `core/` | The server and CLIs (TypeScript). Bundled into a single Bun executable that ships inside ChatterAgent. `src/server/` `src/cli/` `src/player/` |
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
| **Android XR client** (same Unity project. Targets XREAL Aura / Galaxy XR) | Emulator only. Stands in space in OpenXR's Full Space, and the room shows through the background on both glasses and headsets. Connects to the server over LAN. **Not yet verified on real hardware**. See [Try it on XREAL Aura](#try-it-on-xreal-aura-android-xr). |

## Try it on XREAL Aura (Android XR)

The XR build has only run on an emulator so far. **This is the first time it meets real hardware, so bug reports are very welcome.** Follow the steps below from the top.

You need:

- A Mac with Apple Silicon, running Claude Code
- `adb`
- An XREAL Aura
- The Mac and the Aura on the same Wi-Fi

**You don't need Unity or Rust.**

### 1. Install

1. **The app for the Aura.** Download `ChatterMascot.apk` from a `mascot-v*` release on the [Releases page](https://github.com/schwarz9791/chatter-agent/releases), then install it. To update, use `adb install -r`.

   Before installing, turn on Developer options and USB debugging on the Aura, and check that `adb devices` on the Mac lists it. If you have an APK you built yourself installed, the signature differs, so run `adb uninstall tech.sukima.chattermascot` first.

   ```bash
   adb install ChatterMascot.apk
   ```

2. **ChatterAgent on the Mac.** Download `ChatterAgent.zip` from an `agent-v*` release, unzip it, and move `ChatterAgent.app` to `/Applications`. The app is ad-hoc signed and not notarized, so Gatekeeper blocks it after download. Do one of the following:
   - Run `xattr -dr com.apple.quarantine /Applications/ChatterAgent.app`
   - Open it once, then go to System Settings → Privacy & Security and click "Open Anyway". Click "Open Anyway" again in the dialog that follows, then authenticate with Touch ID or an administrator password

   The server is bundled, and ChatterAgent starts it when it opens. Right after that, ChatterAgent shows a "Cannot Show the Mascot" warning. That is expected, because these steps don't install the macOS mascot. The CLI player on the Mac then starts speaking; you mute it in "3. Expose the server to the LAN".

3. **The Claude Code plugin.** Follow [Installing the Claude Code plugin](#installing-the-claude-code-plugin), then restart your Claude Code session.

### 2. Pick an English voice

The default synthesis engine, AivisSpeech, speaks Japanese. **If AivisSpeech isn't installed and you leave the default, the text reaches the Aura but no sound plays.**

For an English voice, use [Kokoro-FastAPI](https://github.com/remsky/Kokoro-FastAPI). Set it up with [`docs/kokoro.md`](./docs/kokoro.md), then point the server at it in `~/.config/chatter-agent/config.json`. Create it if it doesn't exist.

```json
{
  "ttsEngine": "openai",
  "kokoroDir": "/absolute/path/to/Kokoro-FastAPI"
}
```

The voice is `kokoroVoiceId` (default `af_heart`).

### 3. Expose the server to the LAN

By default the server listens on `127.0.0.1` only, which the Aura can't reach. Add `"host": "0.0.0.0"` to the same `config.json`, then choose "Restart Server" in ChatterAgent's menu.

```json
{
  "ttsEngine": "openai",
  "kokoroDir": "/absolute/path/to/Kokoro-FastAPI",
  "host": "0.0.0.0"
}
```

- **If macOS asks for Local Network access, allow it, then choose "Restart Server" once more.** A server started before you allowed it can't be found by the Aura.
- If you start the server from a terminal instead, first choose "Quit" in ChatterAgent's menu (only one server can run at a time), then run the bundled executable with `CHATTER_AGENT_HOST=0.0.0.0 /Applications/ChatterAgent.app/Contents/MacOS/chatter-agent server`. The environment variable takes priority over `config.json`. **In that case the Local Network permission belongs to the terminal app, and you restart the server in the terminal too** (ChatterAgent's "Restart Server" doesn't affect a server running outside it).
- **Turn on "Mute" in ChatterAgent's menu** (and choose "Hide Mascot" if the macOS mascot is showing). Only one client can be connected at a time. If both are connected, the Mac and the Aura take sentences from each other, and the Aura skips some.

### 4. Pair

1. Launch "Chatter Mascot" on the Aura.
2. Choose "Pair with Android…" in ChatterAgent's menu. A 4-digit PIN appears. It is valid for 5 minutes and allows 5 wrong tries. If it expires or you get it wrong 5 times, press "Get a new PIN" in the window.
3. On the Aura, open the settings panel. It floats in front of you. There are two ways to open it:
   - Pinch the character and let go. A gear appears above the head for a short while. Pinch the gear.
   - Turn your palm toward your head. A button appears over your palm. Pinch it with your other hand.
4. In the panel, press "Pair…" under "Connection", enter the PIN, and press "Pair".

On success the token is saved on the Aura and it connects right away. You don't need to restart anything. The Aura finds the Mac over mDNS.

Without ChatterAgent, you can issue a PIN yourself. Run this on the Mac itself (the request has to come from loopback). Without the `Content-Type` header the server answers `415`.

```bash
curl -H 'Content-Type: application/json' -d '{}' http://127.0.0.1:8570/v1/pairing
# {"pin":"2458","expiresAt":1792136026504}
```

### 5. If it doesn't connect

Check these in order.

1. **`host` is still `127.0.0.1`.** The pairing window says "The server isn't exposed to the LAN. …". With curl, you get `409` `{"error":"not_lan"}`. Redo step 3.
2. **macOS denied the Local Network permission.** The permission belongs to the app that started the server (ChatterAgent, or your terminal app). If you chose "Don't Allow" once, the dialog never appears again, and the Mac can reach its own server while the LAN can't. Turn that app on in System Settings → Privacy & Security → Local Network, then choose "Restart Server" (or restart it in the terminal if you started it there).
3. **mDNS doesn't reach the Aura.** This happens when the router doesn't pass multicast (mDNS), or when the Mac and the Aura are on different subnets or VLANs. The panel says "Couldn't find the server." Clone the repository and write the server address to the Aura directly:

   ```bash
   git clone https://github.com/schwarz9791/chatter-agent.git && cd chatter-agent
   ADB="$(command -v adb)" ./chatter-mascot/scripts/configure-android.sh ws://<mac-ip>:8570
   ```

   - The script doesn't look for `adb` on its own (its default is Android Studio's SDK path), so pass it with `ADB=`.
   - The server must be running, because the script reads its token. The token is written too, so you don't need to pair.
   - The server log ("Open Log") lists the addresses to try as `ws://<ip>:<port>` lines. Use the one on the Aura's network.
   - The script restarts the app after writing.

   If it still doesn't connect, the Wi-Fi may be isolating clients (common on guest networks). Isolation blocks all traffic between devices, so naming the address doesn't help. Use a network without isolation.

### 6. Logs for a bug report

- **Aura:** `adb logcat -s Unity ChatterMascot > aura.log`. Start it before launching the app, reproduce the problem, then stop it with Ctrl-C. `ChatterMascot` is the Java-side tag, and the mDNS discovery logs under it, so you need both.
- **Mac:** choose "Open Log" in ChatterAgent's menu (the server log).
- **Which build:** the `[Mascot] version:` line in the startup log, or the end of the XR settings panel.

## Building

Prebuilt APKs, the macOS mascot, and ChatterAgent (macOS) are on the [Releases page](https://github.com/schwarz9791/chatter-agent/releases).

**Run every command from the repository root.**

### Prerequisites

- **Node 24.11 or later** (pinned to 24.19.0 in `mise.toml`)
- **Bun** (pinned in `mise.toml`) — to build ChatterAgent (it compiles the core into one executable and bundles it)
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

This produces the CLI, the server and the player under `core/dist/`. **This is for development** (you don't need it if you use ChatterAgent). You use the output when pointing ChatterAgent's `devCoreDir` at it, when setting the plugin's `CHATTER_AGENT_CLI`, or when starting things with `npm run start:server` / `npm run start:player` (running without ChatterAgent).

### ChatterAgent (macOS)

```bash
(cd core && npm install)
cd chatter-agent-app
npm install
npm run build      # compiles the core with Bun → build/ChatterAgent.app
```

The menu bar app that starts and stops the server and hosts the settings panel. The server and player are bundled in the app. How to use it is under "Running" → "Server".

### macOS client

```bash
cd chatter-mascot
./scripts/build.sh           # → Build/ChatterMascot.app
```

A prebuilt one is on the [Releases page](https://github.com/schwarz9791/chatter-agent/releases).

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
- **Node 24.11 or later** — only to build and run from a clone (the plugin itself doesn't need Node)
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

The plugin's hook runs the executable bundled in ChatterAgent, so **speech starts after you have opened ChatterAgent once** (it records the executable's path each time it launches).

Without ChatterAgent (Linux, or running the server by hand), build the core in a clone (`cd core && npm install && npm run build`) and set `CHATTER_AGENT_CLI=<clone>/core/dist/chatter-agent-speak.mjs` in Claude Code's environment. This path needs Node 24.11 or later.

To update, refresh the marketplace and then update the plugin. Updates arrive when the plugin's
`version` goes up, which happens only when the hook (the contents of `plugin/`) changes.

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

On macOS, opening **ChatterAgent** (a menu bar app) starts the server (from the Releases page, or `chatter-agent-app/build/ChatterAgent.app` built as in "Building"). It starts
its bundled server with the same environment a login shell gives you, and can also stop and restart it. Its settings panel (menu → "Settings…")
edits the server settings and the mascot settings (model, size, volume, motion, and so on). Right-clicking the mascot, or reopening ChatterAgent from Finder, brings the settings window to the front too.

You can also start it by hand from a terminal. Only one server can run at a time, so first choose "Quit" in ChatterAgent's menu
(ChatterAgent shows a server running outside it as such and leaves it alone).

```bash
/Applications/ChatterAgent.app/Contents/MacOS/chatter-agent server

# Without ChatterAgent (Linux, etc.): in a clone (see "Server and CLIs" under "Building")
cd core && npm run start:server
```

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
| A. Mac only | Open ChatterAgent | macOS app / CLI player |
| B. Emulator or a USB-connected device, against the Mac's server | Start it as-is (listens on loopback only by default) | `./scripts/run-android.sh` (goes over `adb reverse`, so it needs neither a token nor exposing the server to the LAN. Keep the device's `connection` empty — `./scripts/configure-android.sh --clear`) |
| C. Over the LAN (a Wi-Fi device) | Add `"host": "0.0.0.0"` to `config.json`, then "Restart Server" in ChatterAgent (see [Expose the server to the LAN](#3-expose-the-server-to-the-lan)) | enter the 4-digit PIN shown by ChatterAgent's "Pair with Android…" into the device's settings panel ("Pair…") on XR, or into the dialog that opens when it can't connect without XR. When mDNS doesn't reach: see [If it doesn't connect](#5-if-it-doesnt-connect) |
| D. Running Mac and Android at once | A second server on its own runtime root and port | Case B or C, pointed at that port |

```bash
# CLI player (audio only. Sound plays without waiting on Unity)
# ChatterAgent starts it itself while the mascot is hidden. Start it by hand only while ChatterAgent isn't running (two at once fight over sentences)
/Applications/ChatterAgent.app/Contents/MacOS/chatter-agent player
# In a clone: cd core && npm run start:player
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

On Android, case C is covered step by step in [Try it on XREAL Aura](#try-it-on-xreal-aura-android-xr).

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
