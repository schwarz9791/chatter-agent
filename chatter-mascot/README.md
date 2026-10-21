# chatter-mascot

**English** | [日本語](./README-ja.md)

The display-side app (Unity + UniVRM). macOS and Android XR from a single project.

For the overall picture (how it works, current state, setup), see the [root README](../README.md).

## Building

**Run every command from the repository root.**

### Prerequisites

- **Unity 6000.3.14f1** — requires the macOS / Android build support modules
- **Unity CLI** (`unity` command) — `chatter-mascot/scripts/*.sh` run the Editor through it
- **Git LFS** — the mascot's bundled model, motion and icons are LFS-tracked. Cloning without it builds with pointer files in their place, and if you rely on the bundled model only a Cube appears (if already cloned, run `git lfs install` then `git lfs pull`, and rebuild)
- **Android SDK platform-tools** (`adb`) — when installing the Android version onto a device

Unity project setup steps are in [`docs/mascot.md`](../docs/mascot.md).

### macOS client

```bash
cd chatter-mascot
./scripts/build.sh           # → Build/ChatterMascot.app
```

A prebuilt one is on the [Releases page](https://github.com/schwarz9791/chatter-agent/releases).

**Right after cloning, finish the setup in [`docs/mascot.md`](../docs/mascot.md) before opening Unity.**
The mascot only renders and plays: it has no settings panel, menu bar item, or shortcuts (and no Dock icon).
Right-clicking the character opens ChatterAgent's settings panel (if ChatterAgent isn't running, it is just launched).

### Android XR client

```bash
cd chatter-mascot
./scripts/build-android.sh   # → Build/ChatterMascot.apk
```

## Running

**Run every command from the repository root.**

### Client

How the server and client pair up depends on the case. See
[`docs/mascot.md`](../docs/mascot.md) ("接続" / Connection) for the details.

| Case | Server | Client |
|---|---|---|
| A. Mac only | Open ChatterAgent | macOS app / CLI player |
| B. Emulator or a USB-connected device, against the Mac's server | Start it as-is (listens on loopback only by default) | `./scripts/run-android.sh` (goes over `adb reverse`, so it needs neither a token nor exposing the server to the LAN. Keep the device's `connection` empty — `./scripts/configure-android.sh --clear`) |
| C. Over the LAN (a Wi-Fi device) | Add `"host": "0.0.0.0"` to `config.json`, then "Restart Server" in ChatterAgent (see [Expose the server to the LAN](../README.md#3-expose-the-server-to-the-lan)) | enter the 4-digit PIN shown by ChatterAgent's "Pair with Android…" into the device's settings panel ("Pair…") on XR, or into the dialog that opens when it can't connect without XR. When mDNS doesn't reach: see [If it doesn't connect](../README.md#5-if-it-doesnt-connect) |
| D. Running Mac and Android at once | A second server on its own runtime root and port | Case B or C, pointed at that port |

```bash
# CLI player (audio only. Sound plays without waiting on Unity)
# ChatterAgent starts it itself while the mascot is hidden. Start it by hand only while ChatterAgent isn't running (two at once fight over sentences)
/Applications/ChatterAgent.app/Contents/MacOS/chatter-agent player
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

On Android, case C is covered step by step in [Try it on XREAL Aura](../README.md#try-it-on-xreal-aura-android-xr).

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

**Models must be VRM 1.0.** A VRM 0.x model can't be loaded, so the bundled model is shown instead, and the log says why (`Failed to load as VRM 1.0`).

**Android XR uses the same directories** (under `Android/data/tech.sukima.chattermascot/files/` on the
device), but **you don't need `adb push`.** Once the connection target and token are configured
(pairing, or `configure-android.sh`), it fetches automatically from the server's `GET /v1/assets` on every launch
(default `auto`; takes effect from the next launch). Manually placed files still work too, and take
priority over synced ones. **The two files directly under those directories are still fixed names: it
reads only `models/mascot.vrm` and `animations/idle.vrma`.** The free-form name in the table above
(`animations/*.vrma`) has no effect there. **The per-category directories (`animations/happy/` and so
on) do take free-form names, whether synced or manually placed.** See
[`docs/mascot.md`](../docs/mascot.md) ("モデルとモーションを入れる").

## Development

```bash
cd chatter-mascot
./scripts/test.sh        # EditMode tests
```
