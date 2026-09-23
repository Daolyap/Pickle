@{
    RootModule        = 'Pickle.psm1'
    ModuleVersion     = '0.1.0'
    GUID              = '5b0d7c3e-6f0a-4d8e-9a57-2f3f0c7c1e11'
    Author            = 'Pickle contributors'
    Description       = 'Core Pickle shell helpers (pk alias and friends). Loaded automatically by Pickle.'
    PowerShellVersion = '7.4'
    FunctionsToExport = '*'
    CmdletsToExport   = @()
    VariablesToExport = @()
    AliasesToExport   = @('pk', 'pickle')
}
