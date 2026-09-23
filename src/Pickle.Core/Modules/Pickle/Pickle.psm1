# Core Pickle module. Cmdlets (Invoke-PickleCommand, Get-PickleConfig, ...) are compiled into Pickle.Core and
# registered in the initial session state; this module adds aliases and script-level helpers.

Set-Alias -Name pk -Value Invoke-PickleCommand
Set-Alias -Name pickle -Value Invoke-PickleCommand

Export-ModuleMember -Function * -Alias pk, pickle
