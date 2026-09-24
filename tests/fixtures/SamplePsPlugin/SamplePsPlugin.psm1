# Test fixture: registers one of each common contribution.

$script:greeting = 'Hello'

function Get-SampleGreeting {
    param([string]$Name = 'world')
    "$script:greeting, $Name!"
}

# Module-private helper: proves registered scriptblocks run in the module's scope.
function Format-SampleShout([string]$Text) { $Text.ToUpperInvariant() }

Register-PickleCommand -Name 'sample-hello' -Description 'Greets from the sample plugin' -Usage 'pk sample-hello [name] [--shout]' -ScriptBlock {
    $name = if ($args.Count -gt 0 -and $args[0] -ne '--shout') { $args[0] } else { 'world' }
    $text = Get-SampleGreeting -Name $name
    if ($args -contains '--shout') { Format-SampleShout $text } else { $text }
}

Register-PicklePromptSegment -Type 'sample' -ScriptBlock {
    param($context)
    [pscustomobject]@{ Text = "sample:$($context.JobCount)"; Foreground = '#FF0000' }
}

Register-PickleHook -Event PostExecute -ScriptBlock {
    $global:SamplePluginLastCommand = $PickleEvent.CommandLine
}

Register-PicklePanel -Id 'sample.panel' -Title 'Sample items' -Key 'Alt+Y' -Items { 'one', 'two' } -Actions @{
    Echo = { Write-Output $_ }
}

Register-PickleCompletion -Name 'sample-targets' -CommandName 'deploy' -ScriptBlock {
    param($word)
    'prod', 'staging', 'dev' | Where-Object { $_ -like "$word*" } | ForEach-Object {
        [pscustomobject]@{ CompletionText = $_; Description = "deploy to $_" }
    }
}

Register-PickleTranslation -Pattern '^please (.+)$' -Replacement '$1' -Name 'sample-please'

Export-ModuleMember -Function Get-SampleGreeting
