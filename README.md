# DownloadManager

A Windows download manager (C# / .NET 8 WPF) with a browser extension that hands downloads to the
app and puts a **Download** button over videos on web pages.

This is an original implementation. Its design was informed by studying how Internet Download
Manager's browser integration behaves; no IDM code, assets or protocol are reused.

## Download

Get the installer from the [latest release](https://github.com/Mudassirdbs/download-manager/releases/latest):

| Your PC | File |
|---|---|
| Windows 10 (1607+) or 11, Intel/AMD — almost everyone | [`DownloadManager-Setup-1.0.0-x64.exe`](https://github.com/Mudassirdbs/download-manager/releases/download/v1.0.0/DownloadManager-Setup-1.0.0-x64.exe) |
| Windows 11 on ARM (Snapdragon, Surface Pro X/11) | [`DownloadManager-Setup-1.0.0-arm64.exe`](https://github.com/Mudassirdbs/download-manager/releases/download/v1.0.0/DownloadManager-Setup-1.0.0-arm64.exe) |

Not sure which? *Settings → System → About → System type* says "x64-based" or "ARM-based".

1. Run the installer. It installs for your user only (no administrator rights) and includes everything it
   needs, including .NET, FFmpeg and yt-dlp.
2. Windows may show **"Windows protected your PC"** because the installer is not code-signed yet: choose
   **More info → Run anyway**.
3. On first start, the Setup window shows how to add the browser extension (Chrome, Edge, Brave). Until the
   extension is in the browser stores, it is loaded from the app's folder in Developer mode.

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
