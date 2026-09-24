using System.Collections;
using Pickle.Abstractions;

namespace Pickle.Core.Prompt;

/// <summary>Maps a theme onto $PSStyle so formatted output (tables, errors, Get-ChildItem) matches the prompt.</summary>
public static class PsStyleSync
{
    public const string Script = """
        param([hashtable] $Formatting, [hashtable] $FileInfo)
        if ($null -eq $PSStyle) { return }
        foreach ($key in $Formatting.Keys) { $PSStyle.Formatting.$key = $Formatting[$key] }
        foreach ($key in $FileInfo.Keys) { $PSStyle.FileInfo.$key = $FileInfo[$key] }
        """;

    public static Hashtable Formatting(Theme theme) => new()
    {
        ["TableHeader"] = Ansi.Style(theme.Ui.Accent, bold: true),
        ["CustomTableHeaderLabel"] = Ansi.Style(theme.Ui.Accent, bold: true, italic: true),
        ["FormatAccent"] = Ansi.Style(theme.Ui.Accent, bold: true),
        ["ErrorAccent"] = Ansi.Style(theme.Ui.Info, bold: true),
        ["Error"] = Ansi.Style(theme.Ui.Error, bold: true),
        ["Warning"] = Ansi.Style(theme.Ui.Warning, bold: true),
        ["Verbose"] = Ansi.Style(theme.Ui.Info),
        ["Debug"] = Ansi.Style(theme.Ui.Muted),
    };

    public static Hashtable FileInfo(Theme theme) => new()
    {
        ["Directory"] = Ansi.Style(theme.Syntax.Type ?? theme.Ui.Info, bold: true),
        ["SymbolicLink"] = Ansi.Style(theme.Syntax.Variable ?? theme.Ui.Info, bold: true),
        ["Executable"] = Ansi.Style(theme.Syntax.Command ?? theme.Ui.Success, bold: true),
    };

    public static IReadOnlyDictionary<string, object?> Parameters(Theme theme) => new Dictionary<string, object?>
    {
        ["Formatting"] = Formatting(theme),
        ["FileInfo"] = FileInfo(theme),
    };
}
