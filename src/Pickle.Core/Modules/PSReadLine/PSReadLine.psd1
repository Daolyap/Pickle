@{
    RootModule        = 'PSReadLine.psm1'
    ModuleVersion     = '2.4.5'
    GUID              = '2f5c7a1e-8d4b-4c6e-9a3f-1b7e5d2c8a40'
    Author            = 'Pickle contributors'
    Description       = 'Pickle compatibility shim for PSReadLine: maps common Set-PSReadLineOption/KeyHandler calls onto Pickle''s editor. Pickle does not use the real PSReadLine.'
    PowerShellVersion = '7.4'
    FunctionsToExport = @('Set-PSReadLineOption', 'Get-PSReadLineOption', 'Set-PSReadLineKeyHandler', 'Get-PSReadLineKeyHandler', 'Remove-PSReadLineKeyHandler')
    CmdletsToExport   = @()
    VariablesToExport = @()
    AliasesToExport   = @()
}
