# chatter-agent-app

**English** | [日本語](./README-ja.md)

ChatterAgent (Tauri). A menu bar app that starts and stops the server and hosts the settings panel.

For the overall picture (how it works, current state, setup), see the [root README](../README.md).

## Building

**Run every command from the repository root.**

### Prerequisites

- **Node 24.11 or later** — same as the build prerequisites in the [root README](../README.md)
- **Bun** (pinned in `mise.toml`) — to build ChatterAgent (it compiles the core into one executable and bundles it)
- **Rust** (pinned in `chatter-agent-app/mise.toml`) — to build ChatterAgent

### ChatterAgent (macOS)

```bash
(cd core && npm install)
cd chatter-agent-app
npm install
npm run build      # compiles the core with Bun → build/ChatterAgent.app
```

The menu bar app that starts and stops the server and hosts the settings panel. The server and player are bundled in the app. How to use it is under "Running" → "Server".

## Running

**Run every command from the repository root.**

### Server

On macOS, opening **ChatterAgent** (a menu bar app) starts the server (from the Releases page, or `chatter-agent-app/build/ChatterAgent.app` built as in "Building"). It starts
its bundled server with the same environment a login shell gives you, and can also stop and restart it. Its settings panel (menu → "Settings…")
edits the server settings and the mascot settings (model, size, volume, motion, and so on). Right-clicking the mascot, or reopening ChatterAgent from Finder, brings the settings window to the front too.

You can also start it by hand from a terminal. Only one server can run at a time, so first choose "Quit" in ChatterAgent's menu
(ChatterAgent shows a server running outside it as such and leaves it alone).

```bash
/Applications/ChatterAgent.app/Contents/MacOS/chatter-agent server
```

"Show Mascot / Hide Mascot" and "Mute" in the menu also switch which client is connected. While the
mascot is shown, the macOS app plays the speech; while it is hidden, the CLI player plays the voice
only; when it is hidden and muted, nothing is connected (connecting both would play everything twice,
so ChatterAgent runs only one of them). The macOS app is looked up as `ChatterMascot.app` in
`/Applications`, then `~/Applications`, then `chatter-mascot/Build`. Shortcuts work too (by default
⌃⌥M toggles mute and ⌃⌥H shows or hides the mascot; only ChatterAgent registers them). Details are in
[`docs/agent.md`](../docs/agent.md).

### Updates

ChatterAgent asks GitHub for new releases at launch and every 6 hours (you can turn this off under "About Chatter Agent" in the settings window).
When one is found the menu item becomes "Update Available…"; choosing it downloads, verifies the signature, swaps the apps in, and restarts
(the mascot in `/Applications` or `~/Applications` is replaced too). "Check for Updates…" still works with the switch off.
Local builds (`0.0.0-dev`) are not updated.

## Development

**Run every command from the repository root.**

```bash
cd chatter-agent-app/src-tauri
cargo fmt --check && cargo clippy --all-targets -- -D warnings && cargo test
cd .. && npm run build:web && npm test   # settings window (tsc + vite build, node --test)
npm run build            # → ChatterAgent.app
```
