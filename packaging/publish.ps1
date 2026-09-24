#!/usr/bin/env pwsh
# Publish a self-contained single-file, ReadyToRun pickle binary for one runtime identifier.
#   ./packaging/publish.ps1 -Rid win-x64 [-Output artifacts/publish/win-x64] [-Version 1.2.3]
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')] [string] $Rid,
    [string] $Output = "artifacts/publish/$Rid",
    [string] $Version
)

$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

$arguments = @(
    'publish', 'src/Pickle/Pickle.csproj',
    '-c', 'Release',
    '-r', $Rid,
    '-o', $Output,
    '-p:PublishSingleFile=true',
    '-p:PublishReadyToRun=true',
    '-p:DebugType=none',
    '-p:DebugSymbols=false'
)
if ($Version) { $arguments += "-p:Version=$Version" }

dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
Get-ChildItem $Output -Filter *.pdb | Remove-Item -Force
Get-ChildItem $Output | Format-Table Name, Length
