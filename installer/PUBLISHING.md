# Giving DownloadManager to other people

## 1. Build

```powershell
powershell -ExecutionPolicy Bypass -File installer\build.ps1
```

Produces in `publish\`:

| File | For |
|---|---|
| `DownloadManager-Setup-<ver>-x64.exe` | Windows 10 (1607+) and 11 on Intel/AMD — almost everyone |
| `DownloadManager-Setup-<ver>-arm64.exe` | Windows 11 on ARM (Surface Pro X/11, Snapdragon laptops) |
| `store\DownloadManager-chromium-<ver>.zip` | Chrome Web Store and Edge Add-ons |
| `store\DownloadManager-firefox-<ver>.zip` | addons.mozilla.org |

The installers bundle the .NET runtime, install per user without administrator rights, add a Start menu entry
and an uninstaller, and optionally start the app with Windows. Silent install: `Setup.exe /VERYSILENT`.

Bump `<Version>` in `Directory.Build.props` before each release; the app, installer and extension all use it.

## 2. Publish the extension

Chrome and Edge only install extensions from their stores for regular users (Developer mode works but warns on
every start). Each store gives the extension its **own ID**, so after publishing:

1. Put the IDs and store pages in `installer\distribution.json`:

   ```json
   {
     "chromiumExtensionIds": ["<chrome-web-store-id>", "<edge-add-ons-id>"],
     "firefoxExtensionIds": ["downloadmanager@local"],
     "chromeStoreUrl": "https://chromewebstore.google.com/detail/<id>",
     "edgeStoreUrl": "https://microsoftedge.microsoft.com/addons/detail/<id>",
     "firefoxStoreUrl": "https://addons.mozilla.org/firefox/addon/<slug>/"
   }
   ```

2. Rebuild the installers. The app registers the native host for every listed ID, and the first-run Setup
   window shows buttons for the store pages. The ID of the unpacked development copy always stays allowed.

Already-installed copies pick up a new `distribution.json` when the app restarts.

### Chrome Web Store
- Developer account: <https://chrome.google.com/webstore/devconsole> (one-time US$5 fee).
- Upload `DownloadManager-chromium-<ver>.zip`. Category: *Productivity*. Single purpose: *"Send downloads and
  page videos to the DownloadManager desktop app."*
- Copy the item ID from the dashboard into `distribution.json`.

### Microsoft Edge Add-ons
- Partner Center: <https://partner.microsoft.com/dashboard/microsoftedge> (free). Upload the same chromium zip.

### Firefox (addons.mozilla.org)
- <https://addons.mozilla.org/developers/> (free). Upload `DownloadManager-firefox-<ver>.zip`; Mozilla signs it.
  The ID `downloadmanager@local` comes from the manifest and is already allowed. If you change it to an address
  you own (recommended, e.g. `downloadmanager@yourdomain.com`), update it in `extension\manifest.json`,
  `src\DownloadManager.Core\Protocol.cs` (`FirefoxExtensionId`) and `distribution.json`.

### Permission justifications (asked by all three stores)

| Permission | Why |
|---|---|
| `nativeMessaging` | Talks to the DownloadManager desktop app; nothing is sent anywhere else. |
| `downloads` | Hands a browser download to the app instead of the browser's own downloader (Alt+click skips this). |
| `webRequest` + host access `<all_urls>` | Notices video/audio responses on the page so the Download button can offer them. Requests are only observed, never modified or blocked. |
| `cookies` | Passes the site's cookies to the app so downloads that need a signed-in session work. Cookies go only to the local app. |
| `contextMenus` | "Download with DownloadManager" on links, images and videos. |
| `tabs`, `scripting` | Attaches the Download button to tabs that were open before the extension was installed. |
| `storage` | Remembers the on/off switch. |

### Privacy policy (the stores require a public URL)

Host this text anywhere public (a GitHub page or gist works) and link it in each listing:

> **DownloadManager Integration — Privacy policy.**
> The extension does not collect, store or transmit personal data to the developer or any third party.
> When you start a download or press the Download button, the address of the file, the page address, the page
> title and the cookies for that site are passed to the DownloadManager application on your own computer
> through the browser's native messaging, so it can download the file the same way the browser would. The
> application keeps its download list on your computer only. No analytics or tracking are used.

## 3. Bundled tools and licenses

The installers ship FFmpeg and yt-dlp, so you are redistributing them:

- **FFmpeg** is the LGPL v2.1+ "shared" build from BtbN/FFmpeg-Builds (no GPL components; libraries are separate
  DLLs). `THIRD-PARTY-NOTICES.txt` names the exact FFmpeg commit and links its source, and `licenses\` holds the
  license text. LGPL distribution also requires the source to stay obtainable; the notice links the upstream
  commit and says it will be provided on request, so keep that promise (or host a copy of the source).
- **yt-dlp** is public domain (Unlicense); its exe bundles Python and other components whose licenses are in
  `licenses\yt-dlp-THIRD_PARTY_LICENSES.txt`.
- Both files are listed in the Start menu ("DownloadManager third-party notices"). This is the usual practice,
  not legal advice.

## 4. Code signing (recommended before wide distribution)

Unsigned installers trigger Windows SmartScreen ("Windows protected your PC" → *More info* → *Run anyway*)
until enough people have run them. A code-signing certificate removes most of that friction:

- Azure Trusted Signing (about US$10/month) or an OV/EV certificate from a CA.
- Sign `DownloadManager.exe`, `dmhost.exe` and the setup files with `signtool sign /fd sha256 /tr <timestamp-url> /td sha256 …`;
  Inno Setup can call signtool through its `SignTool=` directive.

## 5. What users need

- Windows 10 version 1607 or later, or Windows 11 (x64 or ARM64). Nothing else; .NET is included.
- Nothing else to download: ffmpeg (LGPL build) and yt-dlp are bundled in `tools\`. yt-dlp is refreshed in the
  background once the bundled copy is three days old (into `%LOCALAPPDATA%\DownloadManager\tools`, which is
  searched first). Rebuild with `-RefreshTools` to bundle the newest yt-dlp in a release.
- Logs for troubleshooting: `%LOCALAPPDATA%\DownloadManager\logs`.
