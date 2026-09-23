using System.Collections.ObjectModel;
using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Security;
using System.Text;
using Pickle.Abstractions;

namespace Pickle.Core.Hosting;

/// <summary>Host UI: all output from cmdlets, prompts (Read-Host, choices, credentials) and progress go through here.</summary>
public sealed class PickleHostUserInterface : PSHostUserInterface, IHostUISupportsMultipleChoiceSelection
{
    private readonly PickleRuntime _runtime;
    private readonly PickleRawUserInterface _rawUI;
    private readonly ProgressPane _progress;
    private readonly object _writeGate = new();

    public PickleHostUserInterface(PickleRuntime runtime)
    {
        _runtime = runtime;
        _rawUI = new PickleRawUserInterface(runtime);
        _progress = new ProgressPane(runtime);
    }

    public override PSHostRawUserInterface RawUI => _rawUI;

    public override bool SupportsVirtualTerminal => _runtime.Terminal.SupportsAnsi;

    private Theme Theme => _runtime.Themes.Current;

    // ───────────── Output ─────────────

    public override void Write(string value)
    {
        lock (_writeGate)
        {
            _progress.ClearIfVisible();
            _runtime.Terminal.Write(NormalizeNewlines(value));
        }
    }

    public override void Write(ConsoleColor foregroundColor, ConsoleColor backgroundColor, string value)
    {
        var fg = ConsoleColorSgr(foregroundColor, background: false);
        var bg = ConsoleColorSgr(backgroundColor, background: true);

        // Write-Host without explicit colors passes the current RawUI colors; don't paint those.
        if (foregroundColor == _rawUI.ForegroundColor)
        {
            fg = null;
        }

        if (backgroundColor == _rawUI.BackgroundColor)
        {
            bg = null;
        }

        if (fg is null && bg is null)
        {
            Write(value);
            return;
        }

        var sgr = string.Join(';', new[] { fg, bg }.Where(s => s is not null));
        Write($"{Ansi.Esc}[{sgr}m{value}{Ansi.Reset}");
    }

    public override void WriteLine(string value) => Write(value + "\n");

    public override void WriteLine() => Write("\n");

    public override void WriteLine(ConsoleColor foregroundColor, ConsoleColor backgroundColor, string value)
    {
        Write(foregroundColor, backgroundColor, value);
        Write("\n");
    }

    public override void WriteErrorLine(string value) => WriteLine(Ansi.Colorize(value, Theme.Ui.Error));

    public override void WriteWarningLine(string message) => WriteLine(Ansi.Colorize("WARNING: " + message, Theme.Ui.Warning));

    public override void WriteVerboseLine(string message) => WriteLine(Ansi.Colorize("VERBOSE: " + message, Theme.Ui.Warning));

    public override void WriteDebugLine(string message) => WriteLine(Ansi.Colorize("DEBUG: " + message, Theme.Ui.Warning));

    public override void WriteInformation(InformationRecord record)
    {
        // Write-Host output arrives through Write(fg, bg, text); other information records are not shown by default.
    }

    public override void WriteProgress(long sourceId, ProgressRecord record)
    {
        lock (_writeGate)
        {
            _progress.Update(sourceId, record);
        }
    }

    /// <summary>Clears any progress line; call when a pipeline ends.</summary>
    public void ResetProgress()
    {
        lock (_writeGate)
        {
            _progress.Reset();
        }
    }

    // ───────────── Input ─────────────

    public override string ReadLine()
    {
        _progress.ClearIfVisible();
        if (!_runtime.Terminal.IsInteractive)
        {
            return Console.In.ReadLine() ?? string.Empty;
        }

        return _runtime.LineEditor.ReadSimpleLine(string.Empty, mask: false) ?? string.Empty;
    }

    public override SecureString ReadLineAsSecureString()
    {
        _progress.ClearIfVisible();
        var text = _runtime.Terminal.IsInteractive
            ? _runtime.LineEditor.ReadSimpleLine(string.Empty, mask: true) ?? string.Empty
            : Console.In.ReadLine() ?? string.Empty;
        var secure = new SecureString();
        foreach (var c in text)
        {
            secure.AppendChar(c);
        }

        secure.MakeReadOnly();
        return secure;
    }

    public override Dictionary<string, PSObject> Prompt(string caption, string message, Collection<FieldDescription> descriptions)
    {
        WriteCaption(caption, message);
        var results = new Dictionary<string, PSObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in descriptions)
        {
            var label = string.IsNullOrEmpty(field.Label) ? field.Name : StripHotkey(field.Label);
            var typeName = field.ParameterTypeFullName ?? typeof(string).FullName!;

            if (typeName == typeof(SecureString).FullName)
            {
                Write(label + ": ");
                results[field.Name] = new PSObject(ReadLineAsSecureString());
            }
            else if (typeName == typeof(PSCredential).FullName)
            {
                results[field.Name] = new PSObject(PromptForCredential(caption, message, null!, string.Empty));
            }
            else if (typeName.EndsWith("[]", StringComparison.Ordinal))
            {
                var items = new List<string>();
                for (var i = 0; ; i++)
                {
                    Write($"{label}[{i}]: ");
                    var line = ReadLine();
                    if (string.IsNullOrEmpty(line))
                    {
                        break;
                    }

                    items.Add(line);
                }

                results[field.Name] = new PSObject(items.ToArray());
            }
            else
            {
                Write(label + ": ");
                var line = ReadLine();
                if (string.IsNullOrEmpty(line) && field.DefaultValue is not null)
                {
                    results[field.Name] = field.DefaultValue;
                    continue;
                }

                var targetType = Type.GetType(typeName);
                object value = line;
                if (targetType is not null && targetType != typeof(string))
                {
                    try
                    {
                        value = LanguagePrimitives.ConvertTo(line, targetType, CultureInfo.InvariantCulture);
                    }
                    catch (PSInvalidCastException)
                    {
                        value = line;
                    }
                }

                results[field.Name] = new PSObject(value);
            }
        }

        return results;
    }

    public override int PromptForChoice(string caption, string message, Collection<ChoiceDescription> choices, int defaultChoice)
    {
        var result = PromptForChoiceCore(caption, message, choices, defaultChoice >= 0 ? [defaultChoice] : [], multiple: false);
        return result.Count > 0 ? result[0] : defaultChoice;
    }

    public Collection<int> PromptForChoice(string? caption, string? message, Collection<ChoiceDescription> choices, IEnumerable<int>? defaultChoices)
    {
        var result = PromptForChoiceCore(caption, message, choices, defaultChoices?.ToList() ?? [], multiple: true);
        return new Collection<int>(result);
    }

    public override PSCredential PromptForCredential(string caption, string message, string userName, string targetName) =>
        PromptForCredential(caption, message, userName, targetName, PSCredentialTypes.Default, PSCredentialUIOptions.Default);

    public override PSCredential PromptForCredential(string caption, string message, string userName, string targetName, PSCredentialTypes allowedCredentialTypes, PSCredentialUIOptions options)
    {
        WriteCaption(caption, message);
        if (string.IsNullOrEmpty(userName))
        {
            Write("User: ");
            userName = ReadLine();
        }
        else
        {
            WriteLine($"User: {userName}");
        }

        Write($"Password for user {userName}: ");
        var password = ReadLineAsSecureString();
        return new PSCredential(userName, password);
    }

    private List<int> PromptForChoiceCore(string? caption, string? message, Collection<ChoiceDescription> choices, List<int> defaults, bool multiple)
    {
        WriteCaption(caption, message);
        var hotkeys = choices.Select((c, i) => GetHotkey(c.Label) ?? (i + 1).ToString(CultureInfo.InvariantCulture)).ToList();
        var line = new StringBuilder();
        for (var i = 0; i < choices.Count; i++)
        {
            var isDefault = defaults.Contains(i);
            var text = $"[{hotkeys[i]}] {StripHotkey(choices[i].Label)}";
            line.Append(isDefault ? Ansi.Colorize(text, Theme.Ui.Accent, bold: true) : text).Append("  ");
        }

        line.Append("[?] Help");
        if (defaults.Count > 0)
        {
            line.Append(" (default is \"").Append(string.Join(",", defaults.Select(d => hotkeys[d]))).Append("\")");
        }

        while (true)
        {
            WriteLine(line.ToString());
            Write(multiple ? "Choices (comma separated): " : "Choice: ");
            var input = ReadLine().Trim();
            if (input.Length == 0)
            {
                return defaults;
            }

            if (input == "?")
            {
                for (var i = 0; i < choices.Count; i++)
                {
                    WriteLine($"{hotkeys[i]} - {choices[i].HelpMessage}");
                }

                continue;
            }

            var picks = new List<int>();
            var ok = true;
            foreach (var part in (multiple ? input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [input]))
            {
                var index = hotkeys.FindIndex(h => h.Equals(part, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                {
                    index = choices.ToList().FindIndex(c => StripHotkey(c.Label).Equals(part, StringComparison.OrdinalIgnoreCase));
                }

                if (index < 0)
                {
                    ok = false;
                    break;
                }

                picks.Add(index);
            }

            if (ok && picks.Count > 0)
            {
                return picks;
            }
        }
    }

    private void WriteCaption(string? caption, string? message)
    {
        if (!string.IsNullOrEmpty(caption))
        {
            WriteLine(Ansi.Colorize(caption, Theme.Ui.Accent, bold: true));
        }

        if (!string.IsNullOrEmpty(message))
        {
            WriteLine(message);
        }
    }

    private static string? GetHotkey(string label)
    {
        var i = label.IndexOf('&');
        return i >= 0 && i + 1 < label.Length ? char.ToUpperInvariant(label[i + 1]).ToString() : null;
    }

    private static string StripHotkey(string label) => label.Replace("&", string.Empty, StringComparison.Ordinal);

    private static string NormalizeNewlines(string value) =>
        value.Contains('\r', StringComparison.Ordinal) ? value.Replace("\r\n", "\n", StringComparison.Ordinal) : value;

    private static string? ConsoleColorSgr(ConsoleColor color, bool background)
    {
        var index = color switch
        {
            ConsoleColor.Black => 0,
            ConsoleColor.DarkRed => 1,
            ConsoleColor.DarkGreen => 2,
            ConsoleColor.DarkYellow => 3,
            ConsoleColor.DarkBlue => 4,
            ConsoleColor.DarkMagenta => 5,
            ConsoleColor.DarkCyan => 6,
            ConsoleColor.Gray => 7,
            ConsoleColor.DarkGray => 8,
            ConsoleColor.Red => 9,
            ConsoleColor.Green => 10,
            ConsoleColor.Yellow => 11,
            ConsoleColor.Blue => 12,
            ConsoleColor.Magenta => 13,
            ConsoleColor.Cyan => 14,
            ConsoleColor.White => 15,
            _ => -1,
        };
        if (index < 0)
        {
            return null;
        }

        var c = PickleColor.FromAnsi(index);
        return background ? c.ToBackgroundSgr() : c.ToForegroundSgr();
    }
}
