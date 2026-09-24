#!/usr/bin/env pwsh
# Build the Pickle MSI from a single-file publish directory (Windows only).
#   ./packaging/wix/build-msi.ps1 -Rid win-x64 -PublishDir artifacts/publish/win-x64 [-Output artifacts/msi] [-Version 1.2.3]
# Uses WiX Toolset 5.0.2 (MS-RL). WiX 6+ requires accepting the OSMF EULA — only upgrade if you've accepted it.
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

if (-not $Version) {
    # "Pickle 1.2.3 (PowerShell …)" → 1.2.3
    $Version = ((& $exe --version) -split ' ')[1]
}
$msiVersion = ($Version -split '[-+]')[0]

if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    dotnet tool install --global wix --version 5.0.2
    $env:PATH += [IO.Path]::PathSeparator + (Join-Path $HOME '.dotnet/tools')
}

$work = Join-Path $root 'artifacts/msi-work'
New-Item -ItemType Directory -Force -Path $work, $Output | Out-Null

# Windows Terminal fragment installed for all users; commandline relies on the PATH entry the MSI adds and the icon
# is the pickle.png the MSI puts next to pickle.exe (Terminal expands environment variables in icon paths).
$fragment = Join-Path $work 'pickle.json'
& $exe --write-terminal-fragment $fragment --fragment-commandline 'pickle.exe' --fragment-icon '%ProgramFiles%\Pickle\pickle.png' 2>$null
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $fragment)) {
    @{
        profiles = @(@{
                name        = 'Pickle'
                commandline = 'pickle.exe'
                icon        = '%ProgramFiles%\Pickle\pickle.png'
            })
    } | ConvertTo-Json -Depth 5 | Set-Content -Path $fragment -Encoding utf8
}

$arch = if ($Rid -eq 'win-arm64') { 'arm64' } else { 'x64' }
$msi = Join-Path $Output "pickle-$Version-$Rid.msi"
wix build packaging/wix/Package.wxs `
    -arch $arch `
    -d "Version=$msiVersion" `
    -d "PublishDir=$(Resolve-Path $PublishDir)" `
    -d "FragmentFile=$fragment" `
    -d "AssetsDir=$(Resolve-Path assets/logo)" `
    -o $msi
if ($LASTEXITCODE -ne 0) { throw "wix build failed ($LASTEXITCODE)" }

Get-Item $msi | Format-Table Name, Length
