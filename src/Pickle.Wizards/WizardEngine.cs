using System.Globalization;
using System.Text.RegularExpressions;
using Pickle.Abstractions;

namespace Pickle.Wizards;

/// <summary>
/// Turns (mode, values) into a PowerShell command line and back. Values are keyed by option id; flags are "true",
/// lists are newline-separated, template sub-fields are keyed "optionId.sub". Emission order: global options, the
/// subcommand, positionals with Position &lt; 1 (robocopy source/destination), options in section order, extra
/// (unknown) arguments, then the remaining positionals by Position.
/// </summary>
public static class WizardEngine
{
    public static WizardCommand Build(
        WizardDefinition definition,
        string? modeId,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyList<string>? extraArguments = null)
    {
        var mode = WizardSchema.ResolveMode(definition, modeId);
        var options = WizardSchema.GetOptions(definition, mode);
        var scope = new ValueScope(options, values);
        var builder = new CommandBuilder(scope);

        foreach (var scoped in options.Where(o => o.IsGlobal && !o.Option.IsPositional()))
        {
            builder.EmitOption(scoped.Option);
        }

        foreach (var word in mode?.Subcommand ?? [])
        {
            builder.Add(word, TokenKind.Word);
        }

        var positionals = OrderedPositionals(options).ToList();
        foreach (var option in positionals.Where(p => p.Position < 1))
        {
            builder.EmitPositional(option);
        }

        foreach (var scoped in options.Where(o => !o.IsGlobal && !o.Option.IsPositional()))
        {
            builder.EmitOption(scoped.Option);
        }

        foreach (var extra in extraArguments ?? [])
        {
            builder.EmitExtra(extra);
        }

        foreach (var option in positionals.Where(p => p.Position is null or >= 1))
        {
            builder.EmitPositional(option);
        }

        var line = FormatExecutable(definition.Command);
        if (builder.Tokens.Count > 0)
        {
            line += " " + string.Join(' ', builder.Tokens.Select(Format));
        }

        return new WizardCommand(
            definition.Command,
            [.. builder.Tokens.Select(t => t.Text)],
            line,
            builder.Errors,
            builder.Warnings);
    }

    public static WizardParseResult Parse(WizardDefinition definition, string commandLine) =>
        WizardParser.Parse(definition, commandLine);

    /// <summary>
    /// The canonical form of a value set: inactive (DependsOn) options, empty values and values equal to the default are
    /// dropped; flags become "true"; list items are trimmed of blank lines. Parse returns values in this form.
    /// </summary>
    public static Dictionary<string, string> Normalize(WizardDefinition definition, string? modeId, IReadOnlyDictionary<string, string> values)
    {
        var options = WizardSchema.GetOptions(definition, modeId);
        var scope = new ValueScope(options, values);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var option in options.Select(o => o.Option))
        {
            if (!scope.IsActive(option))
            {
                continue;
            }

            if (option.GetTemplate() is { } template)
            {
                if (scope.Rendered(option) is not null)
                {
                    foreach (var name in template.Placeholders)
                    {
                        if (scope.Raw(option.Id + "." + name) is { Length: > 0 } sub)
                        {
                            result[option.Id + "." + name] = sub;
                        }
                    }
                }

                continue;
            }

            var value = scope.Raw(option.Id);
            if (option.Type == WizardOptionType.Flag)
            {
                if (IsTrue(value))
                {
                    result[option.Id] = "true";
                }
            }
            else if (option.IsMultiValued())
            {
                var items = Items(option, value);
                if (items.Count > 0)
                {
                    result[option.Id] = string.Join('\n', items);
                }
            }
            else if (option.IsRaw() && option.IsPositional())
            {
                if (NormalizeArgumentText(value) is { Length: > 0 } text)
                {
                    result[option.Id] = text;
                }
            }
            else if (!string.IsNullOrEmpty(value) && value != option.Default)
            {
                result[option.Id] = value;
            }
        }

        return result;
    }

    /// <summary>Whether an option's DependsOn condition holds ("id", "id=a|b", "!id").</summary>
    public static bool IsActive(WizardDefinition definition, string? modeId, WizardOption option, IReadOnlyDictionary<string, string> values) =>
        new ValueScope(WizardSchema.GetOptions(definition, modeId), values).IsActive(option);

    /// <summary>Per-option problems for inline display (option id → messages), same rules as <see cref="Build"/>.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ValidateFields(WizardDefinition definition, string? modeId, IReadOnlyDictionary<string, string> values)
    {
        var options = WizardSchema.GetOptions(definition, modeId);
        var builder = new CommandBuilder(new ValueScope(options, values));
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var option in options.Select(o => o.Option))
        {
            builder.Errors.Clear();
            if (option.IsPositional())
            {
                builder.EmitPositional(option);
            }
            else
            {
                builder.EmitOption(option);
            }

            if (builder.Errors.Count > 0)
            {
                result[option.Id] = [.. builder.Errors];
            }
        }

        return result;
    }

    /// <summary>True when <paramref name="commandName"/> (a path, "x.exe", any case) invokes this wizard's tool.</summary>
    public static bool MatchesCommand(WizardDefinition definition, string commandName)
    {
        var name = StripExe(Path.GetFileName(commandName.Trim().Trim('"', '\'')));
        return string.Equals(StripExe(definition.Command), name, StringComparison.OrdinalIgnoreCase)
            || definition.Aliases.Any(a => string.Equals(StripExe(a), name, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsTrue(string? value) =>
        value is not null && (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1"
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase) || value.Equals("on", StringComparison.OrdinalIgnoreCase));

    /// <summary>List items (blank lines dropped); key/value pairs normalized to "key{separator}value".</summary>
    public static IReadOnlyList<string> Items(WizardOption option, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return [];
        }

        var items = value.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Trim().Length > 0);
        return option.Type == WizardOptionType.KeyValueList
            ? [.. items.Select(i => NormalizePair(i, option.KeyValueSeparator))]
            : [.. items];
    }

    public static string NormalizePair(string item, string separator)
    {
        var sep = separator.Trim();
        if (sep.Length == 0)
        {
            sep = separator;
        }

        var index = sep.Length == 0 ? -1 : item.IndexOf(sep, StringComparison.Ordinal);
        if (index < 0)
        {
            return item.Trim();
        }

        var key = item[..index].Trim();
        var value = item[(index + sep.Length)..];
        if (separator.Length > 0 && char.IsWhiteSpace(separator[^1]))
        {
            value = value.TrimStart();
        }

        return key + separator + value;
    }

    internal static IEnumerable<WizardOption> OrderedPositionals(IEnumerable<ScopedOption> options) =>
        options.Where(o => !o.IsGlobal && o.Option.IsPositional())
            .Select(o => o.Option)
            .OrderBy(o => o.Position ?? int.MaxValue);

    internal static string? NormalizeArgumentText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!PowerShellQuoting.IsArgumentList(text))
        {
            return text.Trim();
        }

        var parsed = CommandTokenizer.ParseFirst("x " + text);
        return parsed is null ? text.Trim() : string.Join(' ', parsed.Arguments.Select(a => a.Source));
    }

    internal static (string Id, string[]? Values, bool Negate) ParseDependsOn(string dependsOn)
    {
        var text = dependsOn.Trim();
        var negate = text.StartsWith('!');
        if (negate)
        {
            text = text[1..].Trim();
        }

        var eq = text.IndexOf('=', StringComparison.Ordinal);
        return eq < 0
            ? (text, null, negate)
            : (text[..eq].Trim(), text[(eq + 1)..].Split('|', StringSplitOptions.TrimEntries), negate);
    }

    private static string StripExe(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private static string FormatExecutable(string command) =>
        PowerShellQuoting.NeedsQuoting(command, allowLeadingDash: false) ? "& " + PowerShellQuoting.QuoteLiteral(command) : command;

    private static string Format(ArgToken token) => token.Kind switch
    {
        TokenKind.Raw => token.Text,
        TokenKind.Word => PowerShellQuoting.FormatArgument(token.Text, allowLeadingDash: true),
        _ => PowerShellQuoting.FormatArgument(token.Text),
    };

    internal enum TokenKind
    {
        /// <summary>Flag, subcommand or flag+value: may start with a dash.</summary>
        Word,

        /// <summary>A literal value: quoted when needed, including when it starts with a dash.</summary>
        Value,

        /// <summary>PowerShell source, emitted verbatim (already validated).</summary>
        Raw,
    }

    internal readonly record struct ArgToken(string Text, TokenKind Kind);

    /// <summary>Values plus defaults, template renderings and DependsOn evaluation.</summary>
    internal sealed class ValueScope
    {
        private readonly IReadOnlyDictionary<string, string> _values;
        private readonly Dictionary<string, WizardOption> _byId = new(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> _active = new(StringComparer.Ordinal);
        private readonly HashSet<string> _evaluating = new(StringComparer.Ordinal);

        public ValueScope(IReadOnlyList<ScopedOption> options, IReadOnlyDictionary<string, string> values)
        {
            _values = values;
            foreach (var scoped in options)
            {
                _byId.TryAdd(scoped.Option.Id, scoped.Option);
            }
        }

        public string? Raw(string key) => _values.TryGetValue(key, out var v) ? v : null;

        public string? Rendered(WizardOption option) =>
            option.GetTemplate()?.Render(name => Raw(option.Id + "." + name), option.IsRaw());

        /// <summary>The value as the tool sees it: set value, else the default.</summary>
        public string? Effective(WizardOption option)
        {
            if (option.GetTemplate() is not null)
            {
                return Rendered(option);
            }

            var value = Raw(option.Id);
            if (option.Type == WizardOptionType.Flag)
            {
                return IsTrue(value) ? "true" : null;
            }

            return string.IsNullOrEmpty(value) ? option.Default : value;
        }

        public bool IsActive(WizardOption option)
        {
            if (string.IsNullOrWhiteSpace(option.DependsOn))
            {
                return true;
            }

            if (_active.TryGetValue(option.Id, out var cached))
            {
                return cached;
            }

            if (!_evaluating.Add(option.Id))
            {
                return false;
            }

            var (id, allowed, negate) = ParseDependsOn(option.DependsOn);
            var satisfied = false;
            if (_byId.TryGetValue(id, out var dependency) && IsActive(dependency))
            {
                var value = Effective(dependency);
                satisfied = allowed is null
                    ? !string.IsNullOrEmpty(value) && !value.Equals("false", StringComparison.OrdinalIgnoreCase)
                    : allowed.Any(a => string.Equals(a, value ?? string.Empty, StringComparison.OrdinalIgnoreCase));
            }

            _evaluating.Remove(option.Id);
            var active = negate ? !satisfied : satisfied;
            _active[option.Id] = active;
            return active;
        }
    }

    private sealed class CommandBuilder(ValueScope scope)
    {
        public List<ArgToken> Tokens { get; } = [];

        public List<string> Errors { get; } = [];

        public List<string> Warnings { get; } = [];

        public void Add(string text, TokenKind kind) => Tokens.Add(new ArgToken(text, kind));

        public void EmitOption(WizardOption option)
        {
            if (!scope.IsActive(option))
            {
                return;
            }

            var emitted = false;
            var value = scope.Raw(option.Id);
            switch (option.Type)
            {
                case WizardOptionType.Flag:
                    if (IsTrue(value) && option.Flag is { } flag)
                    {
                        Add(flag, TokenKind.Word);
                        emitted = true;
                    }

                    break;
                case WizardOptionType.List:
                case WizardOptionType.KeyValueList:
                    foreach (var item in Items(option, value))
                    {
                        emitted |= EmitValue(option, item);
                    }

                    break;
                default:
                    if (option.GetTemplate() is not null)
                    {
                        if (RenderTemplate(option) is { } rendered)
                        {
                            emitted = EmitValue(option, rendered);
                        }
                    }
                    else if (!string.IsNullOrEmpty(value) && value != option.Default)
                    {
                        if (option.IsFlagChoice())
                        {
                            Add(value, TokenKind.Word);
                            emitted = true;
                        }
                        else
                        {
                            emitted = EmitValue(option, value);
                        }
                    }

                    break;
            }

            Finish(option, emitted);
        }

        public void EmitPositional(WizardOption option)
        {
            if (!scope.IsActive(option))
            {
                return;
            }

            var value = scope.Raw(option.Id);
            var emitted = false;
            if (option.IsRaw())
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    AddMarker(option);
                    if (PowerShellQuoting.IsArgumentList(value))
                    {
                        Add(value.Trim(), TokenKind.Raw);
                    }
                    else
                    {
                        Errors.Add($"{option.Label}: not valid PowerShell arguments (quote text that contains ; | & or newlines).");
                        Add(value, TokenKind.Value);
                    }

                    emitted = true;
                }
            }
            else if (option.IsMultiValued())
            {
                var items = Items(option, value);
                if (items.Count > 0)
                {
                    AddMarker(option);
                    foreach (var item in items)
                    {
                        Validate(option, item);
                        Add(item, TokenKind.Value);
                    }

                    emitted = true;
                }
            }
            else
            {
                var single = option.GetTemplate() is not null ? RenderTemplate(option)
                    : string.IsNullOrEmpty(value) || value == option.Default ? null : value;
                if (single is not null)
                {
                    Validate(option, single);
                    AddMarker(option);
                    Add(single, TokenKind.Value);
                    emitted = true;
                }
            }

            Finish(option, emitted);
        }

        public void EmitExtra(string extra)
        {
            if (string.IsNullOrWhiteSpace(extra))
            {
                return;
            }

            if (PowerShellQuoting.IsArgumentList(extra))
            {
                Add(extra.Trim(), TokenKind.Raw);
            }
            else
            {
                Errors.Add($"Extra arguments: '{extra}' is not valid PowerShell arguments.");
                Add(extra, TokenKind.Value);
            }
        }

        private void AddMarker(WizardOption option)
        {
            if (option.Flag is { } marker)
            {
                Add(marker, TokenKind.Word);
            }
        }

        private string? RenderTemplate(WizardOption option)
        {
            var rendered = scope.Rendered(option);
            if (rendered is null)
            {
                var template = option.GetTemplate()!;
                var filled = template.Placeholders.Where(p => !string.IsNullOrEmpty(scope.Raw(option.Id + "." + p))).ToList();
                if (filled.Count > 0)
                {
                    var missing = template.RequiredPlaceholders.Where(p => !filled.Contains(p));
                    Errors.Add($"{option.Label}: fill in {string.Join(", ", missing.Select(Humanize))}.");
                }
            }

            return rendered;
        }

        private bool EmitValue(WizardOption option, string value)
        {
            Validate(option, value);
            var raw = option.IsRaw();
            if (raw && !PowerShellQuoting.IsSingleArgument(value))
            {
                Errors.Add($"{option.Label}: '{value}' is not a single PowerShell expression.");
                raw = false;
            }

            var flag = option.Flag;
            if (flag is null)
            {
                Add(value, raw ? TokenKind.Raw : TokenKind.Value);
                return true;
            }

            switch (option.ValueStyle.ToLowerInvariant())
            {
                case "equals":
                    Add(flag + "=" + value, raw ? TokenKind.Raw : TokenKind.Word);
                    break;
                case "none":
                    Add(flag + value, raw ? TokenKind.Raw : TokenKind.Word);
                    break;
                default:
                    Add(flag, TokenKind.Word);
                    Add(value, raw ? TokenKind.Raw : TokenKind.Value);
                    break;
            }

            return true;
        }

        private void Validate(WizardOption option, string value)
        {
            if (option.Type == WizardOptionType.Number
                && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                Errors.Add($"{option.Label}: '{value}' is not a number.");
            }

            if (!string.IsNullOrEmpty(option.Validation))
            {
                bool ok;
                try
                {
                    ok = Regex.IsMatch(value, option.Validation, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                }
                catch (ArgumentException)
                {
                    ok = true;
                }
                catch (RegexMatchTimeoutException)
                {
                    ok = true;
                }

                if (!ok)
                {
                    Errors.Add($"{option.Label}: '{value}' is not in the expected format.");
                }
            }
        }

        private void Finish(WizardOption option, bool emitted)
        {
            if (emitted)
            {
                if (option.GetWarning() is { } warning)
                {
                    Warnings.Add($"{option.Label}: {warning}");
                }

                if (option.Type == WizardOptionType.Choice
                    && option.Choices.FirstOrDefault(c => c.Value == scope.Raw(option.Id)) is { } choice
                    && choice.GetWarning() is { } choiceWarning)
                {
                    Warnings.Add($"{option.Label} {choice.Label ?? choice.Value}: {choiceWarning}");
                }
            }
            else if (option.Required && string.IsNullOrEmpty(scope.Effective(option))
                && !Errors.Any(e => e.StartsWith(option.Label + ":", StringComparison.Ordinal)))
            {
                Errors.Add($"{option.Label} is required.");
            }
        }
    }

    internal static string Humanize(string name)
    {
        var chars = new List<char>();
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
            {
                chars.Add(' ');
            }

            chars.Add(i == 0 ? char.ToUpperInvariant(name[i]) : char.ToLowerInvariant(name[i]));
        }

        return new string([.. chars]).Replace('_', ' ');
    }
}
