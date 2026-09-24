using System.Collections;
using System.Globalization;
using System.Management.Automation;
using Pickle.Abstractions;

namespace Pickle.Core.Profile;

/// <summary>
/// Backs the PSReadLine shim module: maps <c>Set-PSReadLineOption</c> / <c>Set-PSReadLineKeyHandler</c> onto Pickle's
/// theme, key bindings and config for this session only (nothing is persisted). Unsupported options warn once.
/// </summary>
public sealed class ReadLineCompat
{
    private static readonly string[] AnsiNames =
    [
        "black", "red", "green", "yellow", "blue", "purple", "cyan", "white",
        "brightBlack", "brightRed", "brightGreen", "brightYellow", "brightBlue", "brightPurple", "brightCyan", "brightWhite",
    ];

    private static readonly Dictionary<ConsoleColor, string> ConsoleColors = new()
    {
        [ConsoleColor.Black] = "black",
        [ConsoleColor.DarkBlue] = "blue",
        [ConsoleColor.DarkGreen] = "green",
        [ConsoleColor.DarkCyan] = "cyan",
        [ConsoleColor.DarkRed] = "red",
        [ConsoleColor.DarkMagenta] = "purple",
        [ConsoleColor.DarkYellow] = "yellow",
        [ConsoleColor.Gray] = "white",
        [ConsoleColor.DarkGray] = "brightBlack",
        [ConsoleColor.Blue] = "brightBlue",
        [ConsoleColor.Green] = "brightGreen",
        [ConsoleColor.Cyan] = "brightCyan",
        [ConsoleColor.Red] = "brightRed",
        [ConsoleColor.Magenta] = "brightPurple",
        [ConsoleColor.Yellow] = "brightYellow",
        [ConsoleColor.White] = "brightWhite",
    };

    private static readonly Dictionary<string, Action<SyntaxColors, string>> ColorSetters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Command"] = (s, v) => s.Command = v,
        ["Comment"] = (s, v) => s.Comment = v,
        ["Default"] = (s, v) => s.Default = v,
        ["Error"] = (s, v) => s.Error = v,
        ["InlinePrediction"] = (s, v) => s.Suggestion = v,
        ["Keyword"] = (s, v) => s.Keyword = v,
        ["Member"] = (s, v) => s.Member = v,
        ["Number"] = (s, v) => s.Number = v,
        ["Operator"] = (s, v) => s.Operator = v,
        ["Parameter"] = (s, v) => s.Parameter = v,
        ["Selection"] = (s, v) => s.SelectionBackground = v,
        ["String"] = (s, v) => s.String = v,
        ["Type"] = (s, v) => s.Type = v,
        ["Variable"] = (s, v) => s.Variable = v,
    };

    private static readonly HashSet<string> IgnoredColors = new(StringComparer.OrdinalIgnoreCase)
    {
        "ContinuationPrompt", "Emphasis", "ListPrediction", "ListPredictionSelected", "ListPredictionTooltip",
    };

    private static readonly HashSet<string> HarmlessOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "ShowToolTips", "HistorySearchCursorMovesToEnd", "HistorySearchCaseSensitive", "MaximumKillRingCount", "CompletionQueryItems",
        "AnsiEscapeTimeout", "DingTone", "DingDuration", "ExtraPromptLineCount", "TerminateOrphanedConsoleApps", "WordDelimiters",
        "HistorySaveStyle", "HistorySavePath", "ContinuationPrompt", "PromptText", "Verbose", "Debug", "ErrorAction", "WarningAction",
        "InformationAction", "ErrorVariable", "WarningVariable", "InformationVariable", "OutVariable", "OutBuffer", "PipelineVariable",
        "ProgressAction",
    };

    private static readonly Dictionary<string, string> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AcceptLine"] = EditorActionNames.AcceptLine,
        ["ValidateAndAcceptLine"] = EditorActionNames.AcceptLine,
        ["AddLine"] = EditorActionNames.InsertNewline,
        ["InsertLineBelow"] = EditorActionNames.InsertNewline,
        ["RevertLine"] = EditorActionNames.ClearLine,
        ["CancelLine"] = EditorActionNames.CancelLine,
        ["CopyOrCancelLine"] = EditorActionNames.CancelLine,
        ["BackwardChar"] = EditorActionNames.BackwardChar,
        ["ForwardChar"] = EditorActionNames.ForwardChar,
        ["BackwardWord"] = EditorActionNames.BackwardWord,
        ["ShellBackwardWord"] = EditorActionNames.BackwardWord,
        ["ForwardWord"] = EditorActionNames.ForwardWord,
        ["NextWord"] = EditorActionNames.ForwardWord,
        ["ShellForwardWord"] = EditorActionNames.ForwardWord,
        ["ShellNextWord"] = EditorActionNames.ForwardWord,
        ["BeginningOfLine"] = EditorActionNames.BeginningOfLine,
        ["EndOfLine"] = EditorActionNames.EndOfLine,
        ["BackwardDeleteChar"] = EditorActionNames.BackwardDeleteChar,
        ["DeleteChar"] = EditorActionNames.DeleteChar,
        ["DeleteCharOrExit"] = EditorActionNames.ExitIfEmpty,
        ["BackwardKillWord"] = EditorActionNames.BackwardKillWord,
        ["UnixWordRubout"] = EditorActionNames.BackwardKillWord,
        ["ShellBackwardKillWord"] = EditorActionNames.BackwardKillWord,
        ["KillWord"] = EditorActionNames.KillWord,
        ["ShellKillWord"] = EditorActionNames.KillWord,
        ["KillLine"] = EditorActionNames.KillToEnd,
        ["ForwardDeleteInput"] = EditorActionNames.KillToEnd,
        ["Undo"] = EditorActionNames.Undo,
        ["Redo"] = EditorActionNames.Redo,
        ["SelectAll"] = EditorActionNames.SelectAll,
        ["SelectBackwardChar"] = EditorActionNames.SelectBackwardChar,
        ["SelectForwardChar"] = EditorActionNames.SelectForwardChar,
        ["SelectBackwardWord"] = EditorActionNames.SelectBackwardWord,
        ["SelectShellBackwardWord"] = EditorActionNames.SelectBackwardWord,
        ["SelectForwardWord"] = EditorActionNames.SelectForwardWord,
        ["SelectNextWord"] = EditorActionNames.SelectForwardWord,
        ["SelectShellForwardWord"] = EditorActionNames.SelectForwardWord,
        ["SelectBackwardsLine"] = EditorActionNames.SelectToStart,
        ["SelectLine"] = EditorActionNames.SelectToEnd,
        ["Copy"] = EditorActionNames.Copy,
        ["Cut"] = EditorActionNames.Cut,
        ["Paste"] = EditorActionNames.Paste,
        ["Yank"] = EditorActionNames.Paste,
        ["PreviousHistory"] = EditorActionNames.HistoryPrevious,
        ["HistorySearchBackward"] = EditorActionNames.HistoryPrevious,
        ["NextHistory"] = EditorActionNames.HistoryNext,
        ["HistorySearchForward"] = EditorActionNames.HistoryNext,
        ["ReverseSearchHistory"] = EditorActionNames.HistorySearch,
        ["ForwardSearchHistory"] = EditorActionNames.HistorySearch,
        ["MenuComplete"] = EditorActionNames.Complete,
        ["TabCompleteNext"] = EditorActionNames.Complete,
        ["Complete"] = EditorActionNames.Complete,
        ["PossibleCompletions"] = EditorActionNames.Complete,
        ["TabCompletePrevious"] = EditorActionNames.CompletePrevious,
        ["AcceptSuggestion"] = EditorActionNames.AcceptSuggestion,
        ["AcceptNextSuggestionWord"] = EditorActionNames.AcceptSuggestionWord,
        ["ClearScreen"] = EditorActionNames.ClearScreen,
    };

    private readonly PickleRuntime _runtime;
    private readonly HashSet<string> _warned = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _colorOverrides = new(StringComparer.OrdinalIgnoreCase);
    private bool _subscribed;

    public ReadLineCompat(PickleRuntime runtime) => _runtime = runtime;

    public void SetOption(IDictionary parameters, Action<string> warn)
    {
        var config = _runtime.Config.Current;
        foreach (DictionaryEntry entry in parameters)
        {
            var name = entry.Key?.ToString() ?? string.Empty;
            var value = Unwrap(entry.Value);
            switch (name.ToLowerInvariant())
            {
                case "editmode":
                    if (!string.Equals(value?.ToString(), "Windows", StringComparison.OrdinalIgnoreCase))
                    {
                        WarnOnce(warn, "editmode", $"Set-PSReadLineOption -EditMode {value}: Pickle's editor has its own key bindings (Windows-style); Vi/Emacs modes aren't supported.");
                    }

                    break;
                case "predictionsource":
                    config.Editor.Autosuggestions = !string.Equals(value?.ToString(), "None", StringComparison.OrdinalIgnoreCase);
                    break;
                case "predictionviewstyle":
                    if (string.Equals(value?.ToString(), "ListView", StringComparison.OrdinalIgnoreCase))
                    {
                        WarnOnce(warn, "listview", "Set-PSReadLineOption -PredictionViewStyle ListView: Pickle shows inline suggestions; use Ctrl+R for the history list.");
                    }

                    break;
                case "historynoduplicates":
                    config.History.IgnoreDuplicates = LanguagePrimitives.IsTrue(value);
                    break;
                case "maximumhistorycount":
                    config.History.MaxEntries = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                    break;
                case "bellstyle":
                    config.Editor.BellStyle = (value?.ToString() ?? "none").ToLowerInvariant();
                    break;
                case "colors":
                    if (value is IDictionary colors)
                    {
                        SetColors(colors, warn);
                    }

                    break;
                case "rest":
                    foreach (var token in (value as IEnumerable)?.Cast<object?>() ?? [])
                    {
                        if (Unwrap(token)?.ToString() is { } text && text.StartsWith('-'))
                        {
                            var option = text.TrimStart('-').TrimEnd(':');
                            WarnOnce(warn, option, $"Set-PSReadLineOption -{option} isn't supported by Pickle and was ignored.");
                        }
                    }

                    break;
                default:
                    if (!HarmlessOptions.Contains(name))
                    {
                        WarnOnce(warn, name, $"Set-PSReadLineOption -{name} isn't supported by Pickle and was ignored.");
                    }

                    break;
            }
        }
    }

    public PSObject GetOption()
    {
        var config = _runtime.Config.Current;
        var syntax = _runtime.Themes.Current.Syntax;
        var result = new PSObject();
        void Add(string name, object? value) => result.Properties.Add(new PSNoteProperty(name, value));
        Add("EditMode", "Windows");
        Add("PredictionSource", config.Editor.Autosuggestions ? "History" : "None");
        Add("PredictionViewStyle", "InlineView");
        Add("HistoryNoDuplicates", config.History.IgnoreDuplicates);
        Add("MaximumHistoryCount", config.History.MaxEntries);
        Add("BellStyle", CultureInfo.InvariantCulture.TextInfo.ToTitleCase(config.Editor.BellStyle));
        Add("HistorySavePath", _runtime.Paths.HistoryFile);
        Add("ShowToolTips", true);
        Add("ContinuationPrompt", _runtime.Themes.Current.Prompt.ContinuationPrompt);
        Add("CommandColor", Ansi.Style(syntax.Command));
        Add("CommentColor", Ansi.Style(syntax.Comment));
        Add("DefaultTokenColor", Ansi.Style(syntax.Default));
        Add("ErrorColor", Ansi.Style(syntax.Error));
        Add("InlinePredictionColor", Ansi.Style(syntax.Suggestion));
        Add("KeywordColor", Ansi.Style(syntax.Keyword));
        Add("MemberColor", Ansi.Style(syntax.Member));
        Add("NumberColor", Ansi.Style(syntax.Number));
        Add("OperatorColor", Ansi.Style(syntax.Operator));
        Add("ParameterColor", Ansi.Style(syntax.Parameter));
        Add("SelectionColor", Ansi.Style(background: syntax.SelectionBackground));
        Add("StringColor", Ansi.Style(syntax.String));
        Add("TypeColor", Ansi.Style(syntax.Type));
        Add("VariableColor", Ansi.Style(syntax.Variable));
        return result;
    }

    public void SetKeyHandler(IEnumerable<string> chords, string? function, bool hasScriptBlock, Action<string> warn)
    {
        foreach (var chord in chords)
        {
            if (!TryParseChord(chord, warn, out var normalized))
            {
                continue;
            }

            if (hasScriptBlock)
            {
                WarnOnce(warn, "scriptblock:" + normalized, $"Set-PSReadLineKeyHandler -ScriptBlock isn't supported by Pickle; '{chord}' keeps its current binding.");
                continue;
            }

            if (string.Equals(function, "Ignore", StringComparison.OrdinalIgnoreCase))
            {
                _runtime.KeyBindingRegistry.Unbind(normalized);
            }
            else if (function is not null && Functions.TryGetValue(function, out var action))
            {
                _runtime.KeyBindingRegistry.Bind(normalized, action);
            }
            else
            {
                WarnOnce(warn, "function:" + function, $"PSReadLine function '{function}' has no Pickle equivalent; '{chord}' was not bound.");
            }
        }
    }

    public IEnumerable<PSObject> GetKeyHandlers(IReadOnlyCollection<string>? chords)
    {
        var wanted = chords?.Select(c => KeyChord.TryParse(c, out var k) ? k.ToString() : c).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, action) in _runtime.KeyBindingRegistry.Bindings.OrderBy(b => b.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (wanted is not null && !wanted.Contains(key))
            {
                continue;
            }

            var result = new PSObject();
            result.Properties.Add(new PSNoteProperty("Key", key));
            result.Properties.Add(new PSNoteProperty("Function", Functions.FirstOrDefault(f => f.Value == action).Key ?? action));
            result.Properties.Add(new PSNoteProperty("Description", _runtime.KeyBindingRegistry.GetAction(action)?.Description ?? string.Empty));
            yield return result;
        }
    }

    public void RemoveKeyHandler(IEnumerable<string> chords, Action<string> warn)
    {
        foreach (var chord in chords)
        {
            if (TryParseChord(chord, warn, out var normalized))
            {
                _runtime.KeyBindingRegistry.Unbind(normalized);
            }
        }
    }

    /// <summary>A PSReadLine color value (ConsoleColor, its name, an ANSI escape sequence, or a Pickle color) as a Pickle color string.</summary>
    public static string? ToPickleColor(object? value, bool background = false)
    {
        value = Unwrap(value);
        if (value is ConsoleColor consoleColor)
        {
            return ConsoleColors[consoleColor];
        }

        var text = value?.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (text.Contains('\u001b', StringComparison.Ordinal))
        {
            return FromSgr(text, background);
        }

        if (Enum.TryParse<ConsoleColor>(text, ignoreCase: true, out var named) && !int.TryParse(text, out _))
        {
            return ConsoleColors[named];
        }

        return PickleColor.Parse(text) is not null ? text : null;
    }

    private void SetColors(IDictionary colors, Action<string> warn)
    {
        foreach (DictionaryEntry entry in colors)
        {
            var key = entry.Key?.ToString() ?? string.Empty;
            if (key.EndsWith("Color", StringComparison.OrdinalIgnoreCase) && key.Length > 5)
            {
                key = key[..^5];
            }

            if (IgnoredColors.Contains(key))
            {
                continue;
            }

            if (!ColorSetters.ContainsKey(key))
            {
                WarnOnce(warn, "color:" + key, $"Set-PSReadLineOption -Colors: '{key}' isn't a color Pickle knows; ignored.");
                continue;
            }

            if (ToPickleColor(entry.Value, background: key.Equals("Selection", StringComparison.OrdinalIgnoreCase)) is not { } color)
            {
                WarnOnce(warn, "colorvalue:" + key, $"Set-PSReadLineOption -Colors: couldn't read the color for '{key}'; ignored.");
                continue;
            }

            _colorOverrides[key] = color;
        }

        ApplyColorOverrides(_runtime.Themes.Current);
        if (!_subscribed)
        {
            _subscribed = true;
            _runtime.Themes.ThemeChanged += (_, theme) => ApplyColorOverrides(theme);
        }
    }

    private void ApplyColorOverrides(Theme theme)
    {
        foreach (var (key, color) in _colorOverrides)
        {
            ColorSetters[key](theme.Syntax, color);
        }
    }

    private bool TryParseChord(string chord, Action<string> warn, out string normalized)
    {
        normalized = string.Empty;
        if (chord.Contains(',', StringComparison.Ordinal) && chord.Trim().Length > 1)
        {
            WarnOnce(warn, "sequence:" + chord, $"Key sequences like '{chord}' aren't supported by Pickle; ignored.");
            return false;
        }

        if (!KeyChord.TryParse(chord, out var parsed))
        {
            WarnOnce(warn, "chord:" + chord, $"Pickle doesn't understand the key '{chord}'; ignored.");
            return false;
        }

        normalized = parsed.ToString();
        return true;
    }

    private void WarnOnce(Action<string> warn, string key, string message)
    {
        lock (_warned)
        {
            if (!_warned.Add(key))
            {
                return;
            }
        }

        warn(message);
    }

    private static object? Unwrap(object? value) => value is PSObject pso ? pso.BaseObject : value;

    private static string? FromSgr(string text, bool background)
    {
        string? fg = null, bg = null;
        var start = text.IndexOf('[', StringComparison.Ordinal);
        var end = text.IndexOf('m', Math.Max(0, start));
        if (start < 0 || end < 0)
        {
            return null;
        }

        var codes = text[(start + 1)..end].Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(c => int.TryParse(c, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1).ToArray();
        for (var i = 0; i < codes.Length; i++)
        {
            var c = codes[i];
            switch (c)
            {
                case >= 30 and <= 37:
                    fg = AnsiNames[c - 30];
                    break;
                case >= 90 and <= 97:
                    fg = AnsiNames[c - 90 + 8];
                    break;
                case >= 40 and <= 47:
                    bg = AnsiNames[c - 40];
                    break;
                case >= 100 and <= 107:
                    bg = AnsiNames[c - 100 + 8];
                    break;
                case 38 or 48 when i + 2 < codes.Length && codes[i + 1] == 5:
                    var indexed = FromIndexed(codes[i + 2]);
                    if (c == 38)
                    {
                        fg = indexed;
                    }
                    else
                    {
                        bg = indexed;
                    }

                    i += 2;
                    break;
                case 38 or 48 when i + 4 < codes.Length && codes[i + 1] == 2:
                    var rgb = $"#{Clamp(codes[i + 2]):X2}{Clamp(codes[i + 3]):X2}{Clamp(codes[i + 4]):X2}";
                    if (c == 38)
                    {
                        fg = rgb;
                    }
                    else
                    {
                        bg = rgb;
                    }

                    i += 4;
                    break;
            }
        }

        return background ? bg : fg;
    }

    private static int Clamp(int value) => Math.Clamp(value, 0, 255);

    private static string? FromIndexed(int n)
    {
        if (n is < 0 or > 255)
        {
            return null;
        }

        if (n < 16)
        {
            return AnsiNames[n];
        }

        if (n >= 232)
        {
            var gray = 8 + (10 * (n - 232));
            return $"#{gray:X2}{gray:X2}{gray:X2}";
        }

        int[] levels = [0, 95, 135, 175, 215, 255];
        n -= 16;
        return $"#{levels[n / 36]:X2}{levels[n / 6 % 6]:X2}{levels[n % 6]:X2}";
    }
}
