# Pickle's PSReadLine shim. Pickle has its own line editor, so these functions translate the common PSReadLine calls
# found in profiles into Pickle settings for the current session (via Invoke-PickleReadLineShim). Unsupported options
# and key handler script blocks produce a one-time warning instead of an error.

function Set-PSReadLineOption {
    [CmdletBinding()]
    param(
        [string]$EditMode,
        [AllowEmptyString()][string]$ContinuationPrompt,
        [switch]$HistoryNoDuplicates,
        [scriptblock]$AddToHistoryHandler,
        [scriptblock]$CommandValidationHandler,
        [switch]$HistorySearchCursorMovesToEnd,
        [int]$MaximumHistoryCount,
        [int]$MaximumKillRingCount,
        [switch]$ShowToolTips,
        [int]$ExtraPromptLineCount,
        [int]$DingTone,
        [int]$DingDuration,
        [string]$BellStyle,
        [int]$CompletionQueryItems,
        [string]$WordDelimiters,
        [switch]$HistorySearchCaseSensitive,
        [string]$HistorySaveStyle,
        [string]$HistorySavePath,
        [int]$AnsiEscapeTimeout,
        [object]$PromptText,
        [string]$ViModeIndicator,
        [scriptblock]$ViModeChangeHandler,
        [hashtable]$Colors,
        [string]$PredictionSource,
        [string]$PredictionViewStyle,
        [switch]$TerminateOrphanedConsoleApps,
        [Parameter(ValueFromRemainingArguments = $true)]$Rest
    )
    Invoke-PickleReadLineShim -Action SetOption -Parameters $PSBoundParameters
}

function Get-PSReadLineOption {
    [CmdletBinding()]
    param()
    Invoke-PickleReadLineShim -Action GetOption
}

function Set-PSReadLineKeyHandler {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)][Alias('Key')][string[]]$Chord,
        [Parameter(Position = 1)][object]$Function,
        [scriptblock]$ScriptBlock,
        [string]$BriefDescription,
        [Alias('LongDescription')][string]$Description,
        [string]$ViMode
    )
    Invoke-PickleReadLineShim -Action SetKeyHandler -Parameters $PSBoundParameters
}

function Get-PSReadLineKeyHandler {
    [CmdletBinding()]
    param(
        [switch]$Bound,
        [switch]$Unbound,
        [Parameter(Position = 0)][Alias('Key')][string[]]$Chord
    )
    Invoke-PickleReadLineShim -Action GetKeyHandler -Parameters $PSBoundParameters
}

function Remove-PSReadLineKeyHandler {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)][Alias('Key')][string[]]$Chord,
        [string]$ViMode
    )
    Invoke-PickleReadLineShim -Action RemoveKeyHandler -Parameters $PSBoundParameters
}

Export-ModuleMember -Function Set-PSReadLineOption, Get-PSReadLineOption, Set-PSReadLineKeyHandler, Get-PSReadLineKeyHandler, Remove-PSReadLineKeyHandler
