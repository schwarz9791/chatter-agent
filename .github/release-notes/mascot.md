ChatterMascot for macOS, Android and Android XR (aimed at XREAL Aura and similar). The XR build has not been run on real hardware yet.

## Install (Android)

```sh
adb install ChatterMascot.apk
```

To update, use `adb install -r ChatterMascot.apk`.

If you have an APK you built yourself (signed with a debug key) installed, the signature differs. Remove it first, then pair again:

```sh
adb uninstall tech.sukima.chattermascot
```

To check the version, look at the end of the settings panel on XR, or the `[Mascot] version:` line in the startup log.

## Install (macOS)

1. Download `ChatterMascot-macOS.zip` and unzip it.
2. Move `ChatterMascot.app` to `/Applications`. ChatterAgent looks there, and in `~/Applications`.
3. The app is ad-hoc signed and not notarized, so Gatekeeper blocks it the first time it opens. Do one of the following:
   - Run `xattr -dr com.apple.quarantine /Applications/ChatterMascot.app`
   - Double-click it (or choose "Show Mascot" in the ChatterAgent menu), then go to System Settings → Privacy & Security and click "Open Anyway". Click "Open Anyway" again in the dialog that follows, then authenticate with Touch ID or an administrator password
4. If the mascot isn't showing, choose "Show Mascot" in the ChatterAgent menu.

To check the version, look at the About section of the ChatterAgent settings window.

## Requirements

You need ChatterAgent on a Mac. Get it from the `agent-v*` releases on the [Releases page](https://github.com/schwarz9791/chatter-agent/releases). Setup is in the [README](https://github.com/schwarz9791/chatter-agent#readme).

## Connect (Android / XR)

1. On the Mac, expose the server to the LAN: set `"host": "0.0.0.0"` in `~/.config/chatter-agent/config.json`, then choose "Restart Server" in the ChatterAgent menu. When macOS asks for Local Network access, allow it and choose "Restart Server" once more.
2. Turn on "Mute" in the ChatterAgent menu (and choose "Hide Mascot" if the macOS mascot is showing). Only one client can be connected at a time; otherwise the Mac and the device take sentences from each other, and the device skips some.
3. Put the Mac and the device on the same Wi-Fi.
4. Choose "Pair with Android…" in the ChatterAgent menu to show a 4-digit PIN.
5. Enter the PIN on the device. On XR, open "Connection" in the settings panel and choose "Pair…". Without XR, a dialog asks for the PIN when the app can't reach the server.
