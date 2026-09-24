@{
    RootModule        = 'Pickle.Translate.psm1'
    ModuleVersion     = '0.1.0'
    GUID              = '9c3e2f4a-6b1d-4f0e-8a7c-3d5b1e2f7a90'
    Author            = 'Pickle contributors'
    Description       = 'POSIX-style commands (ls, grep, cat, rm, ...) implemented over PowerShell cmdlets. Loaded by Pickle on Windows.'
    PowerShellVersion = '7.4'
    FunctionsToExport = @(
        'alias', 'cat', 'chmod', 'clear', 'cp', 'df', 'du', 'env', 'export', 'find', 'grep', 'head', 'history', 'kill',
        'ln', 'ls', 'man', 'mkdir', 'mv', 'open', 'ps', 'rm', 'source', 'tail', 'touch', 'type', 'unalias', 'uname',
        'unset', 'wc', 'which', 'xdg-open'
    )
    CmdletsToExport   = @()
    VariablesToExport = @()
    AliasesToExport   = @()
}
