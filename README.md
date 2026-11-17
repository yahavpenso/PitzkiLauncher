# Pitzki Launcher

Spotlight-style search for the whole PC. Press **Alt+Space** anywhere (changeable from the tray icon).

- Apps: every Start menu / Store app, desktop shortcuts, and all Pitzki apps in C:\Users\Public\Downloads
- Files and folders: Desktop, Documents, Downloads, Pictures, Videos, Music + recently opened files
- Windows settings in Hebrew or English (wifi, bluetooth, מסך, צליל...)
- Actions: lock, sleep, restart, shut down (Enter twice to confirm), recycle bin, task manager, screenshot
- Calculator: type `12*7+3` and Enter copies the answer
- Web: `g something` = Google, `yt something` = YouTube, `site.com` opens it
- Typed in Hebrew by mistake? `נרםצק` still finds Chrome
- Learns what you open most and puts it first

Keys: ↑↓ choose · Enter open · Ctrl+Enter open folder · Ctrl+Shift+Enter run as admin · Esc close

## Download

Get `PitzkiLauncher.exe` from the [Releases page](https://github.com/yahavpenso/PitzkiLauncher/releases/latest) and run it. Nothing else to install. It starts with Windows and waits in the tray; press **Alt+Space** to search.

Part of the Pitzki family: [Pitzki](https://github.com/yahavpenso/Piztki) · [Pitzki Widgets](https://github.com/yahavpenso/PitzkiWidgets).

## Build

`dotnet publish -c Release -r win-x64 --self-contained false -o dist` (copy pitzki.png / pitzki.ico next to the csproj first).
