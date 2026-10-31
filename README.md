# DownloadManager

A Windows download manager (C# / .NET 8 WPF) with a browser extension that hands downloads to the
app and puts a **Download** button over videos on web pages.

This is an independent, open-source project under the [MIT License](LICENSE). It is not affiliated with any
other download manager. See [CONTRIBUTING.md](CONTRIBUTING.md) to help out.

## Download

Open the [latest release](https://github.com/Mudassirdbs/download-manager/releases/latest) and download the
installer for your PC from **Assets**:

| Your PC | File |
|---|---|
| Windows 10 (1607+) or 11, Intel/AMD — almost everyone | `DownloadManager-Setup-<version>-x64.exe` |
| Windows 11 on ARM (Snapdragon, Surface Pro X/11) | `DownloadManager-Setup-<version>-arm64.exe` |

Not sure which? *Settings → System → About → System type* says "x64-based" or "ARM-based".

1. Run the installer. It installs for your user only (no administrator rights) and includes everything it
   needs, including .NET, FFmpeg and yt-dlp.
2. Windows may show **"Windows protected your PC"** because the installer is not code-signed yet: choose
   **More info → Run anyway**.
3. Add the browser extension (Chrome, Edge, Brave). Until it is in the browser stores: open
   `chrome://extensions` (or `edge://extensions`), turn on **Developer mode**, choose **Load unpacked** and pick
   the `extension` folder in DownloadManager's install folder (the first-run Setup window has a button that
   opens it), or unzip `DownloadManager-extension-<version>.zip` from the release and pick that folder.
   Don't load the `DownloadManager-store-…` zips: they are for publishing to the stores and will not connect.

To uninstall, use *Settings → Apps → DownloadManager*.

## Status: milestone 3

- **HLS** (`.m3u8`): built-in engine — quality picker from the master playlist, parallel segment download,
  pause/resume per segment, AES-128 decryption, fMP4 init sections, byte ranges, separate audio renditions;
  remuxed to MP4 with ffmpeg (MPEG-TS kept when ffmpeg is missing). Live streams and DRM are refused.
- **DASH and site pages** (YouTube, Vimeo, …): handled by yt-dlp with the browser's cookies, with a simplified
  quality list (best, per resolution, audio only). Install yt-dlp/ffmpeg from **Tools** in the app (SHA-256 checked).

## Milestone 2

| Area | Included now | Planned |
|---|---|---|
| Engine | up to 32 connections per download with dynamic segment splitting, pause/resume of every segment (saved across restarts), `If-Range` validation that restarts when the file changed, fallback to one connection when ranges are refused, retry with back-off, global and per-download speed limits | per-server connection cap |
| App | download list, "Download File Info" dialog, categories → folders, tray icon, keeps running when closed | queues, scheduler, clipboard monitor, settings window |
| Browser | catches downloads by file type, video/audio sniffing per tab, in-page Download button, toolbar popup, context menu, Alt+click bypass | HLS/DASH (needs segment engine + ffmpeg), site formats via yt-dlp |

Streaming formats (HLS `.m3u8`, DASH `.mpd`) are listed but disabled. DRM-protected video is out of scope.

## Layout

```
src/DownloadManager.Core        engine, list/persistence, bridge protocol (no UI)
src/DownloadManager.NativeHost  dmhost.exe — browser native-messaging host, relays to the app's named pipe
src/DownloadManager.App         DownloadManager.exe — WPF app, pipe server, host registration
tests/DownloadManager.Core.Tests engine tests against a local socket server
extension/                      Manifest V3 extension (Chrome, Edge, Brave; Firefox 121+)
keys/                           extension signing key (private key is git-ignored)
```

## How the pieces talk

```
content.js ──runtime msg──▶ background.js ──native messaging (stdio, length-prefixed JSON)──▶ dmhost.exe
                                                                                               │ named pipe
                                                                     DownloadManager.exe ◀─────┘ DownloadManager.Bridge.<user>
```

- The extension connects at browser start with `launch:false`; if the app is not running it stays
  idle and retries every 30 s, and downloads are left to the browser.
- The video button, popup and context menu connect with `launch:true`, so `dmhost.exe` starts the app.
- `DownloadManager.exe` registers the native host for the current user on every start
  (Chrome, Edge, Brave, Chromium, Vivaldi, Firefox under `HKCU\Software\…\NativeMessagingHosts`).
  `DownloadManager.exe --unregister` removes those entries.

## Releasing to other people

`powershell -ExecutionPolicy Bypass -File installer\build.ps1` builds self-contained x64 and ARM64 installers
and the store packages for the extension. See [installer/PUBLISHING.md](installer/PUBLISHING.md) for store
submission, extension IDs, code signing and what users need.

## Build and run

```bash
dotnet build DownloadManager.sln -c Release
dotnet test tests/DownloadManager.Core.Tests
```

Run `src/DownloadManager.App/bin/Release/net8.0-windows/DownloadManager.exe` once so it registers the host.

### Load the extension

Chrome / Edge / Brave: open `chrome://extensions` (or `edge://extensions`), turn on **Developer mode**,
choose **Load unpacked** and select the `extension` folder. The fixed key in `manifest.json` gives the
ID `fbogbaonaiahnkkinepjjoephghacphe`, which the native host manifest allows.

Firefox: `about:debugging` → This Firefox → **Load Temporary Add-on** → `extension/manifest.json`.

## Contributing

Contributions are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

DownloadManager is released under the [MIT License](LICENSE). Installers also include FFmpeg, yt-dlp and the .NET
runtime under their own licenses, listed in `THIRD-PARTY-NOTICES.txt` in the install folder.
