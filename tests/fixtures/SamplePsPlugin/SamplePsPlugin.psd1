@{
    RootModule        = 'SamplePsPlugin.psm1'
    ModuleVersion     = '1.2.3'
    GUID              = '8d3f0a52-7a0e-4f3c-9d6c-0c2d1b8e4a11'
    Author            = 'Pickle tests'
    Description       = 'Sample PowerShell plugin used by the Pickle tests'
    PowerShellVersion = '7.4'
    FunctionsToExport = @('Get-SampleGreeting')
    CmdletsToExport   = @()
    AliasesToExport   = @()
    PrivateData       = @{
        Pickle = @{
            Id = 'SamplePsPlugin'
        }
    }
}
