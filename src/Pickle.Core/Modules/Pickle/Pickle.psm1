# Core Pickle module. Cmdlets (Invoke-PickleCommand, Get-PickleConfig, Register-PickleCommand, ...) are compiled into
# Pickle.Core and registered in the initial session state; this module adds aliases and script-level helpers.

# A simple (non-advanced) function receives every argument verbatim in $args, so `pk history list -n 5` or
# `pk x -v` reach the pk command instead of binding to cmdlet or common parameters.
function Invoke-Pickle {
    Invoke-PickleCommand -Arguments ([string[]]@($args | ForEach-Object { "$_" }))
}

Set-Alias -Name pk -Value Invoke-Pickle
Set-Alias -Name pickle -Value Invoke-Pickle

function Edit-PickleProfile {
    <#
    .SYNOPSIS
    Open Pickle's profile.ps1 (created if missing) in your editor ($env:EDITOR, VS Code, Notepad, nano or vi).
    #>
    [CmdletBinding()]
    param()
    Invoke-Pickle config edit --profile
}

function Edit-PickleConfig {
    <#
    .SYNOPSIS
    Open config.json (or config.local.json with -Local) in your editor. Pickle reloads it when you save.
    #>
    [CmdletBinding()]
    param([switch]$Local)
    if ($Local) { Invoke-Pickle config edit --local } else { Invoke-Pickle config edit }
}

Export-ModuleMember -Function Invoke-Pickle, Edit-PickleProfile, Edit-PickleConfig -Alias pk, pickle
