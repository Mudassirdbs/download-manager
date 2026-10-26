<#
.SYNOPSIS
  Builds everything needed to give DownloadManager to other people:
    publish\DownloadManager-Setup-<version>-x64.exe     (Windows 10/11, Intel/AMD)
    publish\DownloadManager-Setup-<version>-arm64.exe   (Windows 11 on ARM)
    publish\store\DownloadManager-chromium-<version>.zip (Chrome Web Store and Edge Add-ons upload)
    publish\store\DownloadManager-firefox-<version>.zip  (addons.mozilla.org upload)

  The app is published self-contained, so target machines need no .NET installation.
  Requires the .NET 8 SDK and Inno Setup 6.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File installer\build.ps1
#>
param(
    [string[]]$Runtimes = @("win-x64", "win-arm64"),
    [switch]$SkipTests
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

New-ExtensionPackage "chromium" (Join-Path $staging "extension") $true

foreach ($flavor in "chromium", "firefox") {
    $dir = Join-Path $staging "store-$flavor"
    New-ExtensionPackage $flavor $dir $false
    $zip = Join-Path $store "DownloadManager-$flavor-$version.zip"
    # bsdtar writes forward-slash entry names, which the store validators require.
    & tar.exe -a -c -f $zip -C $dir *
    if ($LASTEXITCODE -ne 0) { throw "Packaging $flavor failed" }
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

    Write-Host "Building installer ($arch)" -ForegroundColor Cyan
    & $iscc "/DAppVersion=$version" "/DArch=$arch" "/DSourceDir=$out" (Join-Path $PSScriptRoot "DownloadManager.iss") | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed for $arch" }
}

Remove-Item $staging -Recurse -Force
Write-Host ""
Write-Host "Done:" -ForegroundColor Green
Get-ChildItem $publish -Recurse -Include *.exe, *.zip -Depth 1 | Where-Object { $_.DirectoryName -notlike "*app-*" } |
    ForEach-Object { "  {0}  ({1:N1} MB)" -f $_.FullName.Substring($root.Length + 1), ($_.Length / 1MB) }
