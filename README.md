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
| [`chatter-mascot/`](./chatter-mascot/README.md) | The display-side app (Unity + UniVRM). macOS and Android XR from a single project |
| [`chatter-agent-app/`](./chatter-agent-app/README.md) | ChatterAgent (Tauri). A menu bar app that starts and stops the server, hosts the settings panel, and shows, hides, mutes, resizes the mascot and previews its motions |
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

Report with the [XREAL Aura test report](https://github.com/schwarz9791/chatter-agent/issues/new?template=xr-test-report.yml) form. Only the environment is required, and "it didn't work" reports are welcome too. Attach these:

- **Aura:** `adb logcat -s Unity ChatterMascot > aura.log`. Start it before launching the app, reproduce the problem, then stop it with Ctrl-C. `ChatterMascot` is the Java-side tag, and the mDNS discovery logs under it, so you need both.
- **Mac:** choose "Open Log" in ChatterAgent's menu (the server log).
- **Which build:** the `[Mascot] version:` line in the startup log, or the end of the XR settings panel.

## Building

Prebuilt APKs, the macOS mascot, and ChatterAgent (macOS) are on the [Releases page](https://github.com/schwarz9791/chatter-agent/releases).

**Run every command from the repository root.**

### Prerequisites

- **Node 24.11 or later** (pinned to 24.19.0 in `mise.toml`)

### Server and CLIs

```bash
cd core
npm install
npm run build
```

This produces the CLI, the server and the player under `core/dist/`. **This is for development** (you don't need it if you use ChatterAgent). To try a branch's core, point ChatterAgent's `devCoreDir` at it, or the plugin's `CHATTER_AGENT_CLI`.

### ChatterAgent (macOS)

How to build ChatterAgent is in [`chatter-agent-app/README.md`](./chatter-agent-app/README.md#building).

### Clients (macOS / Android XR)

How to build the mascot (macOS client and Android XR client), and prerequisites such as Unity, are in [`chatter-mascot/README.md`](./chatter-mascot/README.md#building).

## Running

**Run every command from the repository root.**

### Prerequisites

- **Claude Code**
- **Node 24.11 or later** — only to build from a clone (the plugin itself doesn't need Node)
- **[AivisSpeech](https://aivis-project.com/)** — **just having it installed is enough.** If the synthesis engine isn't running, the server wakes it up, and stopping the server takes it down too. You only need to start it by hand to connect to an engine on a different host, or **to add a speaker** (adding a voice model needs the GUI). To turn the auto-start off entirely, set `CHATTER_AGENT_TTS_SPAWN=0`
- **The synthesis engine is swappable** (`ttsEngine`). The default is AivisSpeech (VOICEVOX-compatible); for languages other than Japanese, **[Kokoro-FastAPI](https://github.com/remsky/Kokoro-FastAPI)** (OpenAI-compatible) is also supported. Setup steps are in [`docs/kokoro.md`](./docs/kokoro.md)
- **macOS** — the display apps target macOS and Android XR, the playback command defaults to `afplay`, and the engine is auto-discovered only at macOS paths
- **Summarization (optional)** — shortens long messages before reading them aloud. The default backend is `fm` (the Apple Foundation Models CLI, macOS 27+). Where it's unavailable (pre-macOS 27), the original text is read as-is. `aiSummaryBackend` can be switched to `claude` (`claude -p`) instead
- **Emotion classification (optional)** — picks an expression for each utterance. The default is **[Ollaya](https://ollaya.dev/)** (a local decision model). Run `ollaya pull laya:multilingual` once to fetch the model; after that the server wakes it up whenever it isn't running, the same way it does for AivisSpeech. Where it's not installed or unreachable, classification falls back to the dictionary-based classifier, so speech never stops. `emotionClassifier` can also be switched to `fm` or `dictionary` (the original rule-based classifier)

### Installing the Claude Code plugin

**Straight from GitHub** (`--sparse` fetches only the plugin part):

```bash
claude plugin marketplace add schwarz9791/chatter-agent --sparse .claude-plugin plugin
claude plugin install chatter-agent@chatter-agent
```

The plugin's hook runs the executable bundled in ChatterAgent, so **speech starts after you have opened ChatterAgent once** (it records the executable's path each time it launches).

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

### Server and clients

How to use the server (ChatterAgent) is in [`chatter-agent-app/README.md`](./chatter-agent-app/README.md#running); how to connect clients is in [`chatter-mascot/README.md`](./chatter-mascot/README.md#running).

## Models and motion

Where to put models and motions is in [`chatter-mascot/README.md`](./chatter-mascot/README.md#models-and-motion).

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

Mascot tests are in [`chatter-mascot/README.md`](./chatter-mascot/README.md#development); ChatterAgent lint and tests are in [`chatter-agent-app/README.md`](./chatter-agent-app/README.md#development).

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

## License

[Apache-2.0](./LICENSE). A derivative of [cc-mascot](https://github.com/kazakago/cc-mascot) (Apache-2.0, Copyright 2026 kazakago). Attribution is in [`NOTICE`](./NOTICE), and porting details are in [`docs/origin.md`](./docs/origin.md).
