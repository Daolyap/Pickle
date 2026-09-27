#!/usr/bin/env pwsh
# Build the Pickle MSI from a single-file publish directory (Windows only).
#   ./packaging/wix/build-msi.ps1 -Rid win-x64 -PublishDir artifacts/publish/win-x64 [-Output artifacts/msi] [-Version 1.2.3]
# Uses WiX Toolset 5.0.2 (MS-RL) with its UI and Util extensions. WiX 6+ requires accepting the OSMF EULA — only
# upgrade if you've accepted it. Downloads the Cascadia Code release once into artifacts/font-cache (SHA-256 pinned).
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('win-x64', 'win-arm64')] [string] $Rid,
    [Parameter(Mandatory)] [string] $PublishDir,
    [string] $Output = 'artifacts/msi',
    [string] $Version
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '../..')
Set-Location $root

$exe = Join-Path $PublishDir 'pickle.exe'
if (-not (Test-Path $exe)) { throw "pickle.exe not found in $PublishDir" }

# A win-arm64 pickle.exe cannot start on an x64 build agent (Windows only emulates the other way round). What the
# script asks of it (version, the fragment smoke test) does not depend on the architecture, so run a host build from source.
$hostArch = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture
$canRun = -not ($Rid -eq 'win-arm64' -and $hostArch -ne [Runtime.InteropServices.Architecture]::Arm64)
function Invoke-Pickle {
    if ($canRun) { & $exe @args }
    else { dotnet run --project src/Pickle/Pickle.csproj -c Release -p:PickleBundleModules=false -- @args }
}

if (-not $Version) {
    # "Pickle 1.2.3 (PowerShell …)" → 1.2.3
    $Version = ((Invoke-Pickle --version) -split ' ')[1]
}
$msiVersion = ($Version -split '[-+]')[0]

if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    dotnet tool install --global wix --version 5.0.2
    $env:PATH += [IO.Path]::PathSeparator + (Join-Path $HOME '.dotnet/tools')
}
foreach ($extension in 'WixToolset.UI.wixext', 'WixToolset.Util.wixext') {
    wix extension add -g "$extension/5.0.2" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "wix extension add $extension failed ($LASTEXITCODE)" }
}

$work = Join-Path $root 'artifacts/msi-work'
New-Item -ItemType Directory -Force -Path $work, $Output | Out-Null

# The Cascadia Code Nerd Font (SIL OFL 1.1) the FontFeature installs. Same release and SHA-256s as
# src/Pickle.Windows/Fonts/FontDownload.cs (CascadiaCodeNerdFont), which installs it per user at runtime.
$fontVersion = '2407.24'
$fontZipSha256 = 'e67a68ee3386db63f48b9054bd196ea752bc6a4ebb4df35adce6733da50c8474'
$fontFiles = @{
    'CascadiaCodeNF.ttf'       = '16d00fae9fc289cda8c9ee4c578ccb4d410f3fa54ec0e5a3901c059b0248a970'
    'CascadiaCodeNFItalic.ttf' = '95fb04b5aaae44c4cb698cfd1b62bddc44e899845f3c494c342d2b0bbf19572e'
}
$fontCache = Join-Path $root 'artifacts/font-cache'
$fontZip = Join-Path $fontCache "CascadiaCode-$fontVersion.zip"
$fontDir = Join-Path $work 'fonts'
New-Item -ItemType Directory -Force -Path $fontCache, $fontDir | Out-Null
if (-not (Test-Path $fontZip) -or (Get-FileHash $fontZip -Algorithm SHA256).Hash -ne $fontZipSha256) {
    Invoke-WebRequest -Uri "https://github.com/microsoft/cascadia-code/releases/download/v$fontVersion/CascadiaCode-$fontVersion.zip" -OutFile $fontZip
    if ((Get-FileHash $fontZip -Algorithm SHA256).Hash -ne $fontZipSha256) { throw "CascadiaCode-$fontVersion.zip failed its SHA-256 check" }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($fontZip)
try {
    foreach ($name in $fontFiles.Keys) {
        $target = Join-Path $fontDir $name
        [IO.Compression.ZipFileExtensions]::ExtractToFile($zip.GetEntry("ttf/$name"), $target, $true)
        if ((Get-FileHash $target -Algorithm SHA256).Hash -ne $fontFiles[$name]) { throw "$name failed its SHA-256 check" }
    }
} finally {
    $zip.Dispose()
}
Copy-Item (Join-Path $PSScriptRoot 'CascadiaCode-LICENSE.txt') (Join-Path $fontDir 'CascadiaCode-LICENSE.txt') -Force

# The MSI writes its Windows Terminal fragment at install time (pickle.exe --write-terminal-fragment, so it names the
# chosen folder and only installed fonts). Run the same command here as a smoke test: CI checks it has the colour scheme.
$fragment = Join-Path $work 'pickle.json'
Remove-Item $fragment -ErrorAction SilentlyContinue
try {
    Invoke-Pickle --write-terminal-fragment $fragment --fragment-executable 'C:\Program Files\Pickle\pickle.exe' --fragment-icon 'C:\Program Files\Pickle\pickle.png' | Out-Host
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $fragment)) { throw "exit code $LASTEXITCODE" }
} catch {
    throw "pickle.exe could not write a Windows Terminal fragment, which the MSI needs at install time: $($_.Exception.Message)"
}

$arch = if ($Rid -eq 'win-arm64') { 'arm64' } else { 'x64' }
$msi = Join-Path $Output "pickle-$Version-$Rid.msi"
wix build packaging/wix/Package.wxs `
    -arch $arch `
    -ext WixToolset.UI.wixext `
    -ext WixToolset.Util.wixext `
    -d "Version=$msiVersion" `
    -d "PublishDir=$(Resolve-Path $PublishDir)" `
    -d "FontDir=$fontDir" `
    -d "AssetsDir=$(Resolve-Path assets/logo)" `
    -o $msi
if ($LASTEXITCODE -ne 0) { throw "wix build failed ($LASTEXITCODE)" }

Get-Item $msi | Format-Table Name, Length
