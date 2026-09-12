# Music Assistant for Windows

A native Windows client for [Music Assistant](https://www.music-assistant.io/), built with the Windows App SDK (WinUI 3).

## Introduction

The app talks to your Music Assistant server over its websocket API, the same one the web interface uses. You sign in with your Music Assistant username and password, the server issues a session token, and the app keeps that token in the Windows Credential Manager. Nothing else is stored on disk besides the server address and your last selected player.

What you get:

- Discover page: players row, Top Picks collage and the server's recommendation rows as paged card rows
- Library: artists, albums, tracks, playlists, audiobooks, podcasts, radio and genres, with filter, favorites and paging
- Album, artist, playlist, podcast, audiobook and genre detail pages
- Search across the library and all providers
- Provider browser
- Full-window Now Playing view with the queue (played / now playing / up next), autoplay and crossfade toggles, clear, jump and remove
- Player bar: play/pause, next/previous, seek, shuffle, repeat, favorite, sound-quality chip, volume, mute, power, queue and a player picker
- Windows media controls: the media overlay, volume flyout and keyboard/headset media keys show and control the active player
- Remote access: away from home the app connects through Music Assistant's relay using the server's Remote ID, end-to-end encrypted and pinned to the server certificate. Local network first, remote fallback, automatically
- Play on this PC: one switch in Settings makes the computer a Music Assistant player (Sendspin), named after the machine, groupable and in sync with your other speakers
- Runs in the notification area: closing the window hides it, the tray icon's right-click menu has Open, Play/Pause, Next, Previous and Exit
- Remembers window size, position and maximized state
- Mouse back/forward buttons navigate
- Live updates from the server (state changes made from any other client show up immediately)
- Automatic reconnect with re-authentication

### Prerequisites

- Windows 10 version 1809 (build 17763) or newer, Windows 11 recommended
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Windows 10 SDK 10.0.26100 (installed with Visual Studio 2022, or via the Windows SDK installer)
- A Music Assistant server (2.7 or newer, with a user account created)

## Install

Download `MusicAssistant-Setup.exe` from the [Releases](../../releases) page and run it. One administrator prompt, then Music Assistant is in the Start Menu. The package is self-contained (no .NET or Windows App SDK runtime needed) and later versions install over the top.

> SmartScreen may show "Windows protected your PC" because the setup exe is not signed with a publicly trusted certificate yet. Choose **More info › Run anyway**.

What the setup exe does: it unpacks `MusicAssistant_x.y.z.0_x64.msix` plus its test certificate and runs `tools\Install-Msix.ps1`, which trusts the certificate in the machine's Trusted People store and installs the package. That is needed because Windows refuses to install an MSIX whose signature it does not trust. If you prefer to use the bare `.msix` from the release, do the same two steps from an elevated PowerShell:

```powershell
Import-Certificate -FilePath .\MusicAssistant-TestSigning.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
Add-AppxPackage .\MusicAssistant_1.0.0.0_x64.msix
```

> Installing the certificate into the *current user's* store, or double-clicking the `.cer` without picking Local Machine, is not enough: the installer reports error `0x800B0109` (root certificate not trusted).

### Getting rid of the certificate step

Windows trusts MSIX signatures from certificates that chain to a public root. Options, cheapest first:

1. **Microsoft Store**: free developer account (one-time fee), the Store signs the package and handles updates. Best for a wide audience.
2. **Azure Trusted Signing**: a low monthly fee, keys stay in Azure, signs with a Microsoft-trusted certificate; `signtool` picks it up. Works for MSIX and for the portable exe (no SmartScreen warning after reputation builds).
3. **A code-signing certificate from a public CA** (DigiCert, Sectigo, SSL.com): OV or EV, yearly cost, hardware token for EV.

With any of those, build with `-p:PackageCertificateThumbprint=<your cert>`; the `.msix` then installs by double-click and the setup exe stops showing the SmartScreen warning.

## Build and run

1. Restore and build:

   ```powershell
   cd MusicAssistant
   dotnet build -c Release -p:Platform=x64 -r win-x64
   ```

2. Run the app:

   ```powershell
   .\bin\x64\Release\net9.0-windows10.0.26100.0\win-x64\MusicAssistant.exe
   ```

3. Enter your server address (for example `http://192.168.1.10:8095`), then either sign in with a Music Assistant username and password or click **Sign in with Home Assistant**. That's it!

> Home Assistant sign-in opens your browser at the Home Assistant address your Music Assistant server is configured with (a local URL or a Nabu Casa remote URL both work). After you approve, the browser lands on a `127.0.0.1` page from this app and the session token is handed back without ever passing through a third party.

> The build is unpackaged and self-contained, so the folder above runs on any machine without installing the Windows App SDK runtime. For Store or MSIX distribution, add a packaging project or flip `WindowsPackageType` in the csproj.

## Remote access

Music Assistant's remote access works like this: the server keeps a websocket open to `signaling.music-assistant.io` and publishes a **Remote ID**, which is a base32 encoding of the first 16 bytes of its own DTLS certificate fingerprint. A client asks the signaling server for that ID, the two exchange a WebRTC offer and answer plus ICE candidates, and a data channel comes up directly between them (through Home Assistant Cloud's TURN servers when the server has a Nabu Casa subscription, otherwise over public STUN). The signaling server only relays the handshake and never sees the session: before the client accepts the answer it strips every non-SHA-256 fingerprint from the SDP and requires every remaining fingerprint to match the Remote ID, so a relay or anyone in between cannot substitute their own certificate.

In this app:

- The WebRTC side lives in `MusicAssistant/Assets/bridge/bridge.js`, a local page hosted in a hidden WebView2 (which ships with the Windows App SDK, so there is no extra dependency and it is the same Chromium WebRTC stack the web app uses). The page is locked down with a strict CSP, cannot navigate anywhere, and talks to the app only through JSON messages.
- `Remote/WebRtcTransport.cs` plugs that channel into the same API client used for local websocket connections. `Remote/LocalImageProxy.cs` serves artwork on `127.0.0.1` under a per-session random path and fetches each image through the data channel, so every image control keeps working unchanged.
- Connection order is always local address first (5 second timeout when a Remote ID is known), then remote. The title bar shows a REMOTE badge while the relay is in use.
- Signing in as an admin over the local network stores the server's Remote ID automatically. Anyone else pastes it once from Settings, or on the login page.
- Home Assistant sign-in is local-only because the browser must reach the server's callback URL. Username and password work over remote.

Run the pinning self-test after touching `bridge.js`:

```powershell
.\tools\bridge-selftest.ps1
```

## Play on this PC (speaker)

Music Assistant streams to players over its own Sendspin protocol: a Noise-encrypted session, device pairing, clock synchronization and timestamped audio chunks. Rather than reimplement that, the app runs the official `@sendspin/sendspin-js` library (Apache-2.0, the same one the web app uses) inside the hidden WebView2 bridge page and lets it play through Web Audio, which comes out of the default Windows output. It is vendored as `MusicAssistant/Assets/bridge/sendspin.js`; `tools/build-sendspin.ps1` rebuilds it with Node.js and esbuild.

- Turn it on under Settings › Play on this PC. The player appears in Music Assistant under the computer's name, pairs automatically, and can be grouped with other players in sync.
- Locally the audio socket is the server's authenticated `/sendspin` proxy. Remotely it is a second data channel on the same WebRTC connection.
- The bridge page runs on an `http://` origin that WebView2 is told to treat as secure, so it can open plain `ws://` sockets to a LAN server while still having WebCodecs for Opus and FLAC decoding.
- `Remote/Speaker.cs` starts and stops the player, completes pairing through the API and mirrors its state for Settings.

## Packaging

`dotnet build` with `-p:WindowsPackageType=MSIX` produces a signed, self-contained MSIX in `dist\`:

```powershell
cd MusicAssistant
dotnet build -c Release -p:Platform=x64 -r win-x64 -p:WindowsPackageType=MSIX -p:AppxPackageSigningEnabled=true -p:PackageCertificateThumbprint=<thumbprint>
```

`tools\make-setup.ps1` then wraps the package, its certificate and `tools\Install-Msix.ps1` into `dist\MusicAssistant-Setup.exe` with IExpress (built into Windows), which is what the release ships.

`Package.appxmanifest` carries the identity (`DevinGreen.MusicAssistant`, publisher `CN=Devin Green`, which must match the certificate subject) and the capabilities: `internetClient`, `privateNetworkClientServer` for the LAN server and loopback listeners, and `runFullTrust`. `tools/make-msix-assets.ps1` regenerates the tile logos from the app icon. For public distribution, sign with a certificate from a public CA or publish through the Microsoft Store; the self-signed certificate is for testing only.

## App icon

The icon source is `MusicAssistant/Assets/music-assistant-fluent.svg`. After editing it, rasterize to a 1024 px transparent PNG and rebuild the `.ico`:

```powershell
.\tools\make-ico.ps1 -Source .\MusicAssistant\Assets\app-1024.png -Out .\MusicAssistant\Assets\app.ico
```

The `.ico` is compiled into the exe and copied next to it for the window and tray icon.

## Protocol self-check

`MusicAssistant.Check` runs the API client against a fake local server and verifies the handshake, login, token auth, partial results, error results, events and image URL building:

```powershell
cd MusicAssistant.Check
dotnet run
```

## Project layout

- `MusicAssistant/Api/MassClient.cs` — websocket client, commands, events, auth, image URLs
- `MusicAssistant/Api/Models.cs` — server data models
- `MusicAssistant/Api/Session.cs` — settings file and Credential Manager token storage
- `MusicAssistant/Api/OAuthLoopback.cs` — one-shot 127.0.0.1 listener that receives the token after Home Assistant sign-in
- `MusicAssistant/MainWindow.xaml` — shell: navigation, content frame, player bar, connection life cycle
- `MusicAssistant/Controls/` — `PlayerBar` (transport) and `MediaRow` (card strip)
- `MusicAssistant/Pages/` — Login, Home, Library, Item, Search, Browse, Queue, Settings
- `MusicAssistant/Templates.xaml` — shared card and row templates with their context menus

## Security notes

- Tokens live in the Windows Credential Manager (`PasswordVault`), never in plain files or logs.
- The app only connects to the address you enter; `http`, `https`, `ws` and `wss` are accepted. Use `https`/`wss` for anything outside your LAN.
- Images are loaded from the server's image proxy or from `https` URLs the server marks as remotely accessible; plain `http` image URLs are routed through the server proxy.
- No third-party NuGet packages beyond the Windows App SDK. The only embedded web content is the app's own local bridge page, loaded from disk under a strict CSP with navigation disabled; it carries one vendored library, `@sendspin/sendspin-js` (Apache-2.0), for the speaker feature.
- Failures in a UI action are logged to `%LOCALAPPDATA%\MusicAssistant\crash.log` and shown in the app instead of terminating it. Tokens and passwords are never logged.

## Memory

Images are decoded at the size they are drawn, long lists virtualize (only visible rows exist), and the runtime uses the workstation non-concurrent garbage collector with `System.GC.ConserveMemory` set. Measured 130 to 160 MB private memory on the Discover page (Release, x64, artwork loaded); roughly 100 MB of that is the WinUI framework itself.

## Not included yet

- Provider and player configuration (use the web interface; Settings links to it)
- Lyrics, Party mode and casting dashboards
