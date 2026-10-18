ChatterAgent is a macOS menu bar app that starts and stops chatter-agent-server and provides a settings panel. The server is bundled; it starts when you open the app.

## Requirements

- A Mac with Apple Silicon (arm64 build)
- Node 24.11 or later, for the Claude Code plugin's hook
- Claude Code with the chatter-agent plugin (see the [README](https://github.com/schwarz9791/chatter-agent#readme))
- [AivisSpeech](https://aivis-project.com/) for the voice. Installing it is enough; the server starts it when needed

## Open the app

The app is ad-hoc signed and not notarized, so Gatekeeper blocks it after download. Move `ChatterAgent.app` to `/Applications`, then do one of the following:

- Run `xattr -dr com.apple.quarantine /Applications/ChatterAgent.app`
- Open it once, then go to System Settings → Privacy & Security and click "Open Anyway". Click "Open Anyway" again in the dialog that follows, then authenticate with Touch ID or an administrator password

## Notes

- When the server is exposed to the LAN, macOS asks for Local Network access for ChatterAgent. Allow it, then choose "Restart Server" in the menu. Headsets can't find a server that was started before you allowed it. If you chose "Don't Allow", turn ChatterAgent on in System Settings → Privacy & Security → Local Network, then restart the server.
- The macOS mascot is a separate download from the `mascot-v*` releases on the [Releases page](https://github.com/schwarz9791/chatter-agent/releases). Put `ChatterMascot.app` in `/Applications`. Without it, ChatterAgent shows a warning and plays the voice on the Mac through the CLI player. When you use a headset, turn on "Mute" in the menu (and choose "Hide Mascot" if the macOS mascot is showing). Only one client can be connected at a time; otherwise the Mac and the headset take sentences from each other, and the headset skips some.
