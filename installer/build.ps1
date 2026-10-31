<#
.SYNOPSIS
  Builds everything needed to give DownloadManager to other people:
    publish\DownloadManager-Setup-<version>-x64.exe     (Windows 10/11, Intel/AMD)
    publish\DownloadManager-Setup-<version>-arm64.exe   (Windows 11 on ARM)
    publish\DownloadManager-extension-<version>.zip      (extension for "Load unpacked"; has the ID the app trusts)
    publish\store\DownloadManager-store-chromium-<version>.zip (Chrome Web Store and Edge Add-ons upload only)
    publish\store\DownloadManager-store-firefox-<version>.zip  (addons.mozilla.org upload only)

  The app is published self-contained, so target machines need no .NET installation. ffmpeg (LGPL build) and
  yt-dlp are bundled, so streams and site videos work right after install. They come from the official
  releases, are checked against the published SHA-256 sums, and are cached in installer\.cache.
  Requires the .NET 8 SDK, Inno Setup 6 and internet access (or a filled cache).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File installer\build.ps1
#>
param(
    [string[]]$Runtimes = @("win-x64", "win-arm64"),
    [switch]$SkipTests,
    # Download the bundled tools again even when a verified copy is cached.
    [switch]$RefreshTools
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root "publish"

function Find-Tool([string]$name, [string[]]$candidates) {
    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    throw "$name was not found. Install it first."
}

$dotnet = Find-Tool "dotnet" @("$env:ProgramFiles\dotnet\dotnet.exe")
$iscc = Find-Tool "ISCC" @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe")
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

[xml]$props = Get-Content (Join-Path $root "Directory.Build.props")
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
Write-Host "DownloadManager $version" -ForegroundColor Cyan

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
New-Item -ItemType Directory -Force $publish | Out-Null

if (-not $SkipTests) {
    & $dotnet test (Join-Path $root "tests\DownloadManager.Core.Tests") -c Release
    if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
}

# ---------------------------------------------------------------- extension packages

$extSource = Join-Path $root "extension"
$manifest = Get-Content (Join-Path $extSource "manifest.json") -Raw | ConvertFrom-Json

function New-ExtensionPackage([string]$flavor, [string]$destination, [bool]$keepKey) {
    if (Test-Path $destination) { Remove-Item $destination -Recurse -Force }
    Copy-Item $extSource $destination -Recurse
    $m = Get-Content (Join-Path $destination "manifest.json") -Raw | ConvertFrom-Json
    $m.version = $version
    # The stores assign their own ID; "key" only pins the ID of the unpacked copy shipped with the app.
    if (-not $keepKey) { $m.PSObject.Properties.Remove("key") }
    if ($flavor -eq "chromium") {
        $m.PSObject.Properties.Remove("browser_specific_settings")
        $m.background = [pscustomobject]@{ service_worker = "background.js" }
    } else {
        $m.PSObject.Properties.Remove("minimum_chrome_version")
        $m.background = [pscustomobject]@{ scripts = @("background.js") }
    }
    [IO.File]::WriteAllText((Join-Path $destination "manifest.json"), ($m | ConvertTo-Json -Depth 10), (New-Object Text.UTF8Encoding $false))
}

$staging = Join-Path $publish "staging"
$store = Join-Path $publish "store"
New-Item -ItemType Directory -Force $store | Out-Null

# The keyed copy has the fixed ID the native host trusts. It ships inside the app and as the release's
# "extension" zip for Load unpacked. The store packages have no key (the stores assign their own ID), so a
# store package loaded unpacked gets a random ID that the app refuses; their names say they are for the stores.
New-ExtensionPackage "chromium" (Join-Path $staging "extension") $true
& tar.exe -a -c -f (Join-Path $publish "DownloadManager-extension-$version.zip") -C (Join-Path $staging "extension") *
if ($LASTEXITCODE -ne 0) { throw "Packaging the unpacked extension failed" }

foreach ($flavor in "chromium", "firefox") {
    $dir = Join-Path $staging "store-$flavor"
    New-ExtensionPackage $flavor $dir $false
    $zip = Join-Path $store "DownloadManager-store-$flavor-$version.zip"
    # bsdtar writes forward-slash entry names, which the store validators require.
    & tar.exe -a -c -f $zip -C $dir *
    if ($LASTEXITCODE -ne 0) { throw "Packaging $flavor failed" }
}

# ---------------------------------------------------------------- bundled tools

# FFmpeg: the LGPL "shared" build from BtbN/FFmpeg-Builds (the Windows build linked from ffmpeg.org). "Shared"
# keeps the LGPL libraries as separate DLLs next to ffmpeg.exe. yt-dlp: the official release executables.
$FfmpegBranch = "9.0"
$cache = Join-Path $PSScriptRoot ".cache"
New-Item -ItemType Directory -Force $cache | Out-Null
$curl = Join-Path $env:SystemRoot "System32\curl.exe"
$utf8 = New-Object Text.UTF8Encoding $false

function Get-Text([string]$url) {
    $lines = & $curl -fsSL --retry 3 $url
    if ($LASTEXITCODE -ne 0) { throw "Download failed: $url" }
    return $lines
}

# A cached copy is reused only while its hash matches the publisher's list.
function Get-Verified([string]$url, [string]$name, [string[]]$sums) {
    $expected = $null
    foreach ($line in $sums) {
        if ($line -match '^([0-9a-fA-F]{64})\s+\*?(\S+)\s*$' -and $Matches[2] -eq $name) { $expected = $Matches[1].ToUpper() }
    }
    if (-not $expected) { throw "$name is not listed in the published checksums" }
    $file = Join-Path $cache $name
    if ($RefreshTools -or -not (Test-Path $file) -or (Get-FileHash $file -Algorithm SHA256).Hash -ne $expected) {
        Write-Host "  downloading $name"
        & $curl -fsSL --retry 3 -o $file $url
        if ($LASTEXITCODE -ne 0) { throw "Download failed: $url" }
    }
    if ((Get-FileHash $file -Algorithm SHA256).Hash -ne $expected) { Remove-Item $file; throw "Checksum mismatch for $name" }
    return $file
}

# The release page redirects to /tag/<version>; unlike the GitHub API this has no anonymous rate limit.
$tagCache = Join-Path $cache "yt-dlp-latest-tag.txt"
$location = (& $curl -fsSI --retry 3 "https://github.com/yt-dlp/yt-dlp/releases/latest") | Where-Object { $_ -match '^location:' }
if ($LASTEXITCODE -eq 0 -and "$location" -match '/tag/([^/\s]+)') {
    $ytTag = $Matches[1]
    Set-Content $tagCache $ytTag
} elseif (Test-Path $tagCache) {
    $ytTag = (Get-Content $tagCache).Trim()
    Write-Warning "Could not check for a newer yt-dlp; using cached version $ytTag"
} else {
    throw "Could not determine the latest yt-dlp version"
}
$ytBase = "https://github.com/yt-dlp/yt-dlp/releases/download/$ytTag"
$ytSums = Get-Text "$ytBase/SHA2-256SUMS"
$ffBase = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest"
$ffSums = Get-Text "$ffBase/checksums.sha256"
Write-Host "Bundling yt-dlp $ytTag and FFmpeg $FfmpegBranch (LGPL)" -ForegroundColor Cyan

function Expand-Ffmpeg([string]$arch) {
    $ffArch = if ($arch -eq "arm64") { "winarm64" } else { "win64" }
    $zipName = "ffmpeg-n$FfmpegBranch-latest-$ffArch-lgpl-shared-$FfmpegBranch.zip"
    $zip = Get-Verified "$ffBase/$zipName" $zipName $ffSums
    $dir = Join-Path $cache ([IO.Path]::GetFileNameWithoutExtension($zipName))
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    Expand-Archive $zip $dir
    return Get-ChildItem $dir -Directory | Select-Object -First 1
}

# The x64 build runs here, so its version line (with the exact FFmpeg commit) describes the release.
$ffX64 = Expand-Ffmpeg "x64"
$ffVersion = ((& (Join-Path $ffX64.FullName "bin\ffmpeg.exe") -hide_banner -version) | Select-Object -First 1) -replace '^ffmpeg version (\S+).*', '$1'
$ffSource = if ($ffVersion -match '-g([0-9a-f]{7,40})') { "https://github.com/FFmpeg/FFmpeg/tree/$($Matches[1])" } else { "https://github.com/FFmpeg/FFmpeg/tree/release/$FfmpegBranch" }

$licenseFiles = @{}
foreach ($f in "LICENSE", "THIRD_PARTY_LICENSES.txt") {
    $path = Join-Path $cache "yt-dlp-$ytTag-$f"
    if (-not (Test-Path $path)) {
        & $curl -fsSL --retry 3 -o $path "https://raw.githubusercontent.com/yt-dlp/yt-dlp/$ytTag/$f"
        if ($LASTEXITCODE -ne 0) { throw "Could not fetch yt-dlp $f" }
    }
    $licenseFiles[$f] = $path
}

$notices = @"
DownloadManager includes the following third-party software.

FFmpeg $ffVersion
  Files:   tools\ffmpeg.exe, tools\ffprobe.exe and the DLLs in tools\
  License: GNU Lesser General Public License, version 2.1 or later (licenses\FFmpeg-LICENSE.txt)
  Build:   LGPL shared build by BtbN/FFmpeg-Builds, https://github.com/BtbN/FFmpeg-Builds
  Source:  $ffSource
  The FFmpeg libraries are separate DLLs and may be replaced with compatible builds of the same version.
  The corresponding source code is available at the address above; it will also be provided on request.

yt-dlp $ytTag
  File:    tools\yt-dlp.exe
  License: The Unlicense (licenses\yt-dlp-LICENSE.txt)
  The executable bundles Python and other components under their own licenses
  (licenses\yt-dlp-THIRD_PARTY_LICENSES.txt). Source: https://github.com/yt-dlp/yt-dlp/tree/$ytTag

.NET runtime
  License: MIT, https://github.com/dotnet/runtime/blob/main/LICENSE.TXT
"@

function Add-Tools([string]$arch, [string]$destination) {
    $tools = Join-Path $destination "tools"
    $licenses = Join-Path $destination "licenses"
    New-Item -ItemType Directory -Force $tools, $licenses | Out-Null

    $ff = if ($arch -eq "x64") { $ffX64 } else { Expand-Ffmpeg $arch }
    # ffplay is a media player the app never uses.
    Get-ChildItem (Join-Path $ff.FullName "bin") | Where-Object { $_.Name -ne "ffplay.exe" } | Copy-Item -Destination $tools
    Copy-Item (Join-Path $ff.FullName "LICENSE.txt") (Join-Path $licenses "FFmpeg-LICENSE.txt")

    $ytName = if ($arch -eq "arm64") { "yt-dlp_arm64.exe" } else { "yt-dlp.exe" }
    Copy-Item (Get-Verified "$ytBase/$ytName" $ytName $ytSums) (Join-Path $tools "yt-dlp.exe")
    Copy-Item $licenseFiles["LICENSE"] (Join-Path $licenses "yt-dlp-LICENSE.txt")
    Copy-Item $licenseFiles["THIRD_PARTY_LICENSES.txt"] (Join-Path $licenses "yt-dlp-THIRD_PARTY_LICENSES.txt")
    [IO.File]::WriteAllText((Join-Path $destination "THIRD-PARTY-NOTICES.txt"), $notices, $utf8)
}

# ---------------------------------------------------------------- app + installers

foreach ($rid in $Runtimes) {
    $arch = $rid.Split("-")[1]
    $out = Join-Path $publish "app-$arch"
    Write-Host "Publishing $rid" -ForegroundColor Cyan
    foreach ($project in "src\DownloadManager.App", "src\DownloadManager.NativeHost") {
        & $dotnet publish (Join-Path $root $project) -c Release -r $rid --self-contained true `
            -p:PublishReadyToRun=true -p:DebugType=none -p:GenerateDocumentationFile=false -o $out
        if ($LASTEXITCODE -ne 0) { throw "Publishing $project for $rid failed" }
    }
    Copy-Item (Join-Path $staging "extension") (Join-Path $out "extension") -Recurse
    Copy-Item (Join-Path $PSScriptRoot "distribution.json") $out
    Add-Tools $arch $out

    Write-Host "Building installer ($arch)" -ForegroundColor Cyan
    & $iscc "/DAppVersion=$version" "/DArch=$arch" "/DSourceDir=$out" (Join-Path $PSScriptRoot "DownloadManager.iss") | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed for $arch" }
}

Remove-Item $staging -Recurse -Force
Write-Host ""
Write-Host "Done:" -ForegroundColor Green
Get-ChildItem $publish -Recurse -Include *.exe, *.zip -Depth 1 | Where-Object { $_.DirectoryName -notlike "*app-*" } |
    ForEach-Object { "  {0}  ({1:N1} MB)" -f $_.FullName.Substring($root.Length + 1), ($_.Length / 1MB) }
