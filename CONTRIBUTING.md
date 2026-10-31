# Contributing to DownloadManager

Thanks for helping. Bug reports, fixes, and small focused improvements are all welcome. For a large change, open
an issue first so we can agree on the approach before you spend time on it.

## Set up

You need Windows 10 or 11, the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), and optionally
[ffmpeg](https://ffmpeg.org/download.html) on `PATH`. The stream tests that need ffmpeg return early without it.

```powershell
dotnet build DownloadManager.sln
dotnet test tests/DownloadManager.Core.Tests
```

## Run it

1. Start `src/DownloadManager.App/bin/Debug/net8.0-windows/DownloadManager.exe` once. It registers the browser
   bridge for your Windows user.
2. In `chrome://extensions` or `edge://extensions`, turn on **Developer mode**, choose **Load unpacked**, and pick
   the `extension` folder.

The public key in `extension/manifest.json` gives every local copy the same ID, so you don't need a private key.

To remove the registration afterwards, run `DownloadManager.exe --unregister`.

## Where things live

| Path | What it holds |
|---|---|
| `src/DownloadManager.Core` | Download engine, streams, tools and bridge protocol; no UI |
| `src/DownloadManager.NativeHost` | `dmhost.exe`, the bridge between the browser and the app |
| `src/DownloadManager.App` | The WPF app |
| `extension` | The Manifest V3 browser extension |
| `tests/DownloadManager.Core.Tests` | Engine tests against a local test server |
| `installer` | Release build script, Inno Setup script and publishing guide |

## Submit a change

1. Fork the repository and create a branch from `main`.
2. Keep the change focused, and match the surrounding code.
3. Add or update tests for engine changes, and run `dotnet test`.
4. Test browser or UI changes by hand, and describe what you checked in the pull request.
5. Use a [Conventional Commit](https://www.conventionalcommits.org/) subject, such as `fix: resume after a dropped connection`.
6. Open a pull request. The automated build and tests must pass before review.

## Project rules

- Do not add code that bypasses DRM or other technical protection measures.
- Do not commit credentials, cookies, signing keys, build output, or downloaded media.
- Only submit code you have the right to contribute. Do not copy code from proprietary software.

## License

By contributing, you agree that your contribution is licensed under the [MIT License](LICENSE).
