# CLAUDE.md

Notes for Claude Code working in this repo. Read [CODING-STANDARDS.md](CODING-STANDARDS.md) before writing code; it holds the C# conventions and Windows app practices this project follows.

## What this is

A native Windows client for [Music Assistant](https://music-assistant.io), built with WinUI 3 (Windows App SDK 1.8) and .NET 9. It talks to a Music Assistant server over its WebSocket API, plays audio on the PC itself as a Sendspin speaker, and reaches the server remotely over WebRTC when it isn't on the same network.

Keep dependencies to the four already in the project file (Windows App SDK, SDK build tools, SIPSorcery, Concentus). Don't add a package for something a few lines of code can do.

## Layout

| Path | What lives there |
| --- | --- |
| `MusicAssistant/Api/` | Server connection, models, session and settings |
| `MusicAssistant/Pages/` | Discover, Browse, Library, Item, Search, Queue, Settings, Login |
| `MusicAssistant/Controls/` | Player bar, media rows, item menus |
| `MusicAssistant/Sendspin/` | This PC as a speaker: Noise handshake, WASAPI output, clock sync, mDNS |
| `MusicAssistant/Remote/` | WebRTC transport for remote access |
| `MusicAssistant/Templates.xaml` | Shared item templates and styles (has a code-behind class) |
| `tools/` | Icon and package asset scripts |

Secrets (tokens, speaker keys) live in Windows Credential Manager, never in settings files or logs. App data is `%LOCALAPPDATA%\MusicAssistant` (settings.json, crash.log, art cache).

## Build and test

Build and install the package, then test in the installed app. A debug run isn't enough, because packaging, the firewall rule and the speaker only behave correctly in the MSIX.

```powershell
dotnet build MusicAssistant\MusicAssistant.csproj -c Release -p:Platform=x64 -p:WindowsPackageType=MSIX -p:AppxPackageSigningEnabled=true -p:PackageCertificateThumbprint=084CD8DC0C61C943728484988754D16512B5BEE9
Add-AppxPackage dist\MusicAssistant_<version>_x64_Test\MusicAssistant_<version>_x64.msix -ForceApplicationShutdown
Start-Process "shell:AppsFolder\artistro08.MusicAssistantNative_80wzptmyxepxj!App"
```

- A higher version installs over the old one. Reinstalling the **same** version fails with 0x80073CFB; remove it first with `Get-AppxPackage artistro08.MusicAssistantNative | Remove-AppxPackage`.
- Reinstalling stops playback on this PC's speaker. Check whether it was playing first, and start it again afterwards.
- The build must end with 0 warnings.

## Checking the UI

- Drive and inspect the app through UI Automation, and capture the window with `PrintWindow(hwnd, hdc, 2)`, which works even when another window covers it.
- Selecting a page through UI Automation leaves the keyboard focus ring in the shot. For clean screenshots, click with the mouse instead, and only when the user has asked for that.
- Hover and animation can't be verified this way. Install the build and ask the user to check those.

## Versioning and releases

The version appears in three places, and all three change together: `MusicAssistant/Package.appxmanifest` (Identity), `MusicAssistant/MusicAssistant.csproj` (`<Version>`), and the install filenames in `README.md`. Patch releases are bug fixes; a fourth number is a rebuild of the same release.

Work goes on a `feature/...` or `fix/...` branch and merges into `main` with `--no-ff`. A release is a tag plus a GitHub release carrying the `.msix` and `MusicAssistant-TestSigning.cer`. Commit, push, merge and publish only when the user asks.

## Things that bite

- The server sends `media_item_updated` **before** it answers the command that caused it, so decide new state before awaiting, not after.
- Queue items carry a snapshot of their track. Favorite flags on them go stale; ask the server.
- Show server state as it is. Don't paper over a provider's quirks in the client.
- `VisualStateManager` setters in a `ControlTemplate` crashed WinUI natively on first hover. Do hover effects in code.
- Model properties must be nullable where the server may omit or null them. One non-nullable bool broke connecting entirely.
