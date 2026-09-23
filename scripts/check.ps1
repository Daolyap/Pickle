#!/usr/bin/env pwsh
# Windows equivalent of scripts/check.sh: restore → build (warnings are errors) → format check → tests.
#   ./scripts/check.ps1 [-Quick] [-Fix] [-Filter <text>]
[CmdletBinding()]
param(
    [switch] $Quick,
    [switch] $Fix,
    [string] $Filter
)

$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Green }
function Invoke-Checked([scriptblock] $block) {
    & $block
    if ($LASTEXITCODE -ne 0) { throw "Command failed with exit code $LASTEXITCODE" }
}

Step 'restore'
Invoke-Checked { dotnet restore Pickle.slnx --verbosity quiet }

Step 'build (warnings are errors)'
Invoke-Checked { dotnet build Pickle.slnx --no-restore --verbosity quiet -clp:ErrorsOnly }

if ($Fix) {
    Step 'format (fixing)'
    Invoke-Checked { dotnet format Pickle.slnx --no-restore }
}
elseif (-not $Quick) {
    Step 'format (verify)'
    Invoke-Checked { dotnet format Pickle.slnx --no-restore --verify-no-changes }
}

Step 'tests'
if ($Filter) {
    Invoke-Checked { dotnet test --solution Pickle.slnx --no-build -- --filter-method "*$Filter*" }
}
else {
    Invoke-Checked { dotnet test --solution Pickle.slnx --no-build }
}

Step 'all checks passed'
