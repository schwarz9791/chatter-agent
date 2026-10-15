ChatterAgent is a macOS menu bar app that starts and stops chatter-agent-server and provides a settings panel.

## Requirements

- A Mac with Apple Silicon (arm64 build)
- Node 24.11 or later
- Claude Code with the chatter-agent plugin (see the [README](https://github.com/schwarz9791/chatter-agent#readme))

## Open the app

The app is ad-hoc signed and not notarized, so Gatekeeper blocks it after download. Move `ChatterAgent.app` to `/Applications`, then do one of the following:

- Run `xattr -dr com.apple.quarantine /Applications/ChatterAgent.app`
- Open it once, then go to System Settings → Privacy & Security and click "Open Anyway"

## Set up core

The core (server and CLIs) is not bundled.

```sh
git clone https://github.com/schwarz9791/chatter-agent.git
cd chatter-agent/core && npm install && npm run build
```

Then choose "Choose Core Folder…" in the menu and select the `core/` folder.

## Notes

- When the server is exposed to the LAN, macOS asks for Local Network access for ChatterAgent. Allow it, or headsets can't find the Mac.
- The macOS mascot is not bundled. If ChatterAgent cannot find it, it shows a warning and plays the voice on the Mac through the CLI player. If you only want to listen on the headset, turn on "Mute" in the menu.
