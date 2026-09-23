using System.Text.RegularExpressions;
using Pickle.Abstractions;

namespace Pickle.Wizards;

/// <summary>
/// Parses a command line (PowerShell syntax) back into wizard values: <c>--x=value</c>, <c>-ovalue</c>, combined short
/// flags (<c>-sSL</c>), <c>-Param:value</c>, repeated list flags, netsh-style <c>key=value</c>, <c>--</c> and positionals.
/// Tokens the wizard can't represent are returned verbatim as unknown tokens.
/// </summary>
internal static partial class WizardParser
{
    public static WizardParseResult Parse(WizardDefinition definition, string commandLine)
    {
        var commands = CommandTokenizer.ParseCommands(commandLine);
        var command = commands.FirstOrDefault(c => WizardEngine.MatchesCommand(definition, c.Name));
        if (command is null)
        {
            return new WizardParseResult(definition.Modes.FirstOrDefault()?.Id, new Dictionary<string, string>(), [])
            {
                Start = 0,
                Length = 0,
                Matched = false,
            };
        }

        var result = Parse(definition, command.Arguments);
        return result with { Start = command.Start, Length = command.Length };
    }

    public static WizardParseResult Parse(WizardDefinition definition, IReadOnlyList<CommandToken> tokens)
    {
        if (definition.Modes.Count == 0)
        {
            return ParseMode(definition, null, tokens, 0, 0);
        }

        WizardParseResult? best = null;
        (int Unknown, int Length, int Index, int Order) bestScore = default;
        for (var order = 0; order < definition.Modes.Count; order++)
        {
            var mode = definition.Modes[order];
            var index = FindSubcommand(tokens, mode.Subcommand);
            if (index < 0)
            {
                continue;
            }

            var result = ParseMode(definition, mode, tokens, index, mode.Subcommand.Count);
            var score = (result.UnknownTokens.Count, -mode.Subcommand.Count, index, order);
            if (best is null || score.CompareTo(bestScore) < 0)
            {
                best = result;
                bestScore = score;
            }
        }

        return best ?? new WizardParseResult(null, new Dictionary<string, string>(), [.. tokens.Select(t => t.Source)]);
    }

    private static int FindSubcommand(IReadOnlyList<CommandToken> tokens, IReadOnlyList<string> subcommand)
    {
        if (subcommand.Count == 0)
        {
            return 0;
        }

        for (var i = 0; i + subcommand.Count <= tokens.Count; i++)
        {
            var ok = true;
            for (var j = 0; j < subcommand.Count && ok; j++)
            {
                ok = tokens[i + j].IsLiteral && string.Equals(tokens[i + j].Value, subcommand[j], StringComparison.OrdinalIgnoreCase);
            }

            if (ok)
            {
                return i;
            }
        }

        return -1;
    }

    private static WizardParseResult ParseMode(WizardDefinition definition, WizardMode? mode, IReadOnlyList<CommandToken> tokens, int subIndex, int subLength)
    {
        var options = WizardSchema.GetOptions(definition, mode);
        var state = new ParseState(options);
        // After the subcommand a mode's own flag wins over a global one with the same spelling (git -c vs switch -c).
        var modeFirst = OptionLookup.Create(options.OrderBy(o => o.IsGlobal));
        if (mode is not null && subLength > 0)
        {
            state.ParseRange(tokens.Take(subIndex).ToList(), OptionLookup.Create(options.Where(o => o.IsGlobal)), allowPositionals: false);
            state.ParseRange(tokens.Skip(subIndex + subLength).ToList(), modeFirst, allowPositionals: true);
        }
        else
        {
            state.ParseRange(tokens, modeFirst, allowPositionals: true);
        }

        state.DistributePositionals();
        var values = state.Finish(definition, mode?.Id);
        return new WizardParseResult(mode?.Id, values, state.Unknown);
    }

    [GeneratedRegex(@"^/[A-Za-z?][A-Za-z0-9?+-]*(:.*)?$")]
    private static partial Regex SlashFlagRegex();

    [GeneratedRegex(@"^-[A-Za-z0-9]{2,}$")]
    private static partial Regex CombinedShortFlagsRegex();

    private sealed record FlagMatch(WizardOption Option, string? ChoiceValue);

    private sealed class OptionLookup
    {
        private readonly Dictionary<string, List<FlagMatch>> _exact = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<FlagMatch>> _insensitive = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(string Flag, WizardOption Option)> _attached = [];

        public Dictionary<string, WizardOption> Markers { get; } = new(StringComparer.Ordinal);

        public bool UsesSlashFlags { get; private set; }

        /// <summary>
        /// False for tools with single-dash long options (ffmpeg -crf, openssl -in, cmdlets): there "-abc" is never a
        /// bundle of -a -b -c and "-ifoo" is not "-i foo".
        /// </summary>
        public bool IsGetoptStyle { get; private set; } = true;

        public static OptionLookup Create(IEnumerable<ScopedOption> options)
        {
            var lookup = new OptionLookup();
            var shortValueOptions = new List<(string Flag, WizardOption Option)>();
            foreach (var option in options.Select(o => o.Option))
            {
                if (option.IsPositional())
                {
                    if (option.Flag is { } marker)
                    {
                        lookup.Markers.TryAdd(marker, option);
                    }

                    continue;
                }

                if (option.IsFlagChoice())
                {
                    foreach (var choice in option.Choices)
                    {
                        lookup.Add(choice.Value, new FlagMatch(option, choice.Value));
                    }

                    continue;
                }

                foreach (var flag in new[] { option.Flag }.Concat(option.FlagAliases).OfType<string>().Where(f => f.Length > 0))
                {
                    lookup.Add(flag, new FlagMatch(option, null));
                    if (option.Type == WizardOptionType.Flag)
                    {
                        continue;
                    }

                    if (option.ValueStyle.Equals("none", StringComparison.OrdinalIgnoreCase))
                    {
                        lookup._attached.Add((flag, option));
                    }
                    else if (IsShortFlag(flag))
                    {
                        shortValueOptions.Add((flag, option));
                    }
                }
            }

            if (lookup.IsGetoptStyle)
            {
                lookup._attached.AddRange(shortValueOptions);
            }

            lookup._attached.Sort((a, b) => b.Flag.Length.CompareTo(a.Flag.Length));
            return lookup;
        }

        public FlagMatch? Find(string token)
        {
            if (_exact.TryGetValue(token, out var exact))
            {
                return exact[0];
            }

            // Case-insensitive only for long/slash/bare flags: -x and -X are different options almost everywhere.
            if (!IsShortFlag(token) && _insensitive.TryGetValue(token, out var matches)
                && matches.Select(m => m.Option).Distinct().Count() == 1)
            {
                return matches[0];
            }

            return null;
        }

        public WizardOption? FindValueOption(string flag) =>
            Find(flag) is { ChoiceValue: null } match && match.Option.Type != WizardOptionType.Flag ? match.Option : null;

        public FlagMatch? FindShort(char c) => _exact.TryGetValue("-" + c, out var matches) ? matches[0] : null;

        public (WizardOption Option, string Value)? FindAttached(string token)
        {
            foreach (var (flag, option) in _attached)
            {
                var comparison = flag.StartsWith('-') ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                if (token.Length > flag.Length && token.StartsWith(flag, comparison))
                {
                    return (option, token[flag.Length..]);
                }
            }

            return null;
        }

        public bool IsFlagLike(string token)
        {
            if (token.Length > 1 && PowerShellQuoting.IsDash(token[0]))
            {
                return true;
            }

            return UsesSlashFlags && SlashFlagRegex().IsMatch(token);
        }

        private void Add(string flag, FlagMatch match)
        {
            if (!_exact.TryGetValue(flag, out var list))
            {
                _exact[flag] = list = [];
            }

            list.Add(match);
            if (!_insensitive.TryGetValue(flag, out var insensitive))
            {
                _insensitive[flag] = insensitive = [];
            }

            insensitive.Add(match);
            UsesSlashFlags |= flag.StartsWith('/');
            if (flag.Length > 2 && flag[0] == '-' && flag[1] != '-' && match.ChoiceValue is null)
            {
                IsGetoptStyle = false;
            }
        }

        private static bool IsShortFlag(string flag) => flag.Length == 2 && flag[0] == '-' && flag[1] != '-';
    }

    private sealed class ParseState(IReadOnlyList<ScopedOption> options)
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _lists = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _sources = new(StringComparer.Ordinal);
        private readonly List<CommandToken> _positionals = [];
        private readonly Dictionary<WizardOption, List<CommandToken>> _markerTokens = [];
        private readonly List<string> _rest = [];
        private readonly List<WizardOption> _ordered = [.. WizardEngine.OrderedPositionals(options)];
        private WizardOption? _restTarget;
        private WizardOption? _markerTarget;

        public List<string> Unknown { get; } = [];

        public void ParseRange(IReadOnlyList<CommandToken> tokens, OptionLookup lookup, bool allowPositionals)
        {
            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.IsRedirection)
                {
                    Unknown.Add(token.Source);
                    continue;
                }

                if (_restTarget is not null)
                {
                    _rest.Add(token.Source);
                    continue;
                }

                if (_markerTarget is not null)
                {
                    _markerTokens[_markerTarget].Add(token);
                    continue;
                }

                if (!token.IsLiteral)
                {
                    Positional(token, allowPositionals);
                    continue;
                }

                var value = token.Value;
                if (value == "--%")
                {
                    Unknown.AddRange(tokens.Skip(i).Select(t => t.Source));
                    return;
                }

                if (allowPositionals && lookup.Markers.TryGetValue(value, out var marker))
                {
                    if (marker.IsRaw())
                    {
                        _restTarget = marker;
                    }
                    else
                    {
                        _markerTarget = marker;
                        _markerTokens[marker] = [];
                    }

                    continue;
                }

                if (value == "--")
                {
                    Unknown.AddRange(tokens.Skip(i).Select(t => t.Source));
                    return;
                }

                if (TryOption(tokens, ref i, lookup))
                {
                    continue;
                }

                if (lookup.IsFlagLike(value))
                {
                    Unknown.Add(token.Source);
                    continue;
                }

                Positional(token, allowPositionals);
            }
        }

        public void DistributePositionals()
        {
            var singles = _ordered.Where(o => o.Flag is null).ToList();
            var variadic = singles.FindIndex(o => o.IsMultiValued());
            if (variadic < 0)
            {
                for (var i = 0; i < _positionals.Count; i++)
                {
                    if (i < singles.Count)
                    {
                        AssignPositional(singles[i], _positionals[i]);
                    }
                    else
                    {
                        Unknown.Add(_positionals[i].Source);
                    }
                }
            }
            else
            {
                var before = singles.Take(variadic).ToList();
                var after = singles.Skip(variadic + 1).ToList();
                var index = 0;
                for (; index < before.Count && index < _positionals.Count; index++)
                {
                    AssignPositional(before[index], _positionals[index]);
                }

                var remaining = _positionals.Count - index;
                var afterCount = Math.Min(after.Count, Math.Max(0, remaining - 1));
                var variadicCount = remaining - afterCount;
                for (var i = 0; i < variadicCount; i++)
                {
                    AssignPositional(singles[variadic], _positionals[index++]);
                }

                for (var i = 0; i < afterCount; i++)
                {
                    AssignPositional(after[i], _positionals[index++]);
                }
            }

            foreach (var (option, tokens) in _markerTokens)
            {
                foreach (var token in tokens)
                {
                    AssignPositional(option, token);
                }
            }

            if (_restTarget is not null && _rest.Count > 0)
            {
                _values[_restTarget.Id] = string.Join(' ', _rest);
                Track(_restTarget, string.Join(' ', _rest));
            }
        }

        public Dictionary<string, string> Finish(WizardDefinition definition, string? modeId)
        {
            var values = new Dictionary<string, string>(_values, StringComparer.Ordinal);
            foreach (var (id, items) in _lists)
            {
                values[id] = string.Join('\n', items);
            }

            // Options whose DependsOn doesn't hold would silently disappear on rebuild; keep them as unknown text.
            var scope = new WizardEngine.ValueScope(options, values);
            foreach (var option in options.Select(o => o.Option))
            {
                if (!scope.IsActive(option) && _sources.TryGetValue(option.Id, out var sources))
                {
                    Unknown.AddRange(sources);
                    values.Remove(option.Id);
                    foreach (var key in values.Keys.Where(k => k.StartsWith(option.Id + ".", StringComparison.Ordinal)).ToList())
                    {
                        values.Remove(key);
                    }
                }
            }

            return WizardEngine.Normalize(definition, modeId, values);
        }

        private void Positional(CommandToken token, bool allowPositionals)
        {
            if (!allowPositionals)
            {
                Unknown.Add(token.Source);
                return;
            }

            var slot = _positionals.Count;
            var singles = _ordered.Where(o => o.Flag is null).ToList();
            var variadicBefore = singles.Take(slot).Any(o => o.IsMultiValued());
            if (!variadicBefore && slot < singles.Count && singles[slot].IsRaw())
            {
                _restTarget = singles[slot];
                _rest.Add(token.Source);
                return;
            }

            _positionals.Add(token);
        }

        private bool TryOption(IReadOnlyList<CommandToken> tokens, ref int i, OptionLookup lookup)
        {
            var token = tokens[i];
            var value = token.Value;

            if (lookup.Find(value) is { } match)
            {
                if (match.ChoiceValue is not null)
                {
                    SetSingle(match.Option, match.ChoiceValue, token.Source);
                    return true;
                }

                if (match.Option.Type == WizardOptionType.Flag)
                {
                    SetFlag(match.Option, token.Source);
                    return true;
                }

                var next = i + 1 < tokens.Count ? tokens[i + 1] : null;
                if (next is null || next.IsRedirection || (next.IsLiteral && lookup.Find(next.Value) is not null))
                {
                    Unknown.Add(token.Source);
                    return true;
                }

                i++;
                Assign(match.Option, ValueOf(match.Option, next), token.Source + " " + next.Source);
                return true;
            }

            var eq = value.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && lookup.FindValueOption(value[..eq]) is { } equalsOption)
            {
                Assign(equalsOption, value[(eq + 1)..], token.Source);
                return true;
            }

            var colon = value.IndexOf(':', StringComparison.Ordinal);
            if (colon > 1 && value[0] == '-' && lookup.FindValueOption(value[..colon]) is { } colonOption)
            {
                Assign(colonOption, value[(colon + 1)..], token.Source);
                return true;
            }

            if (lookup.FindAttached(value) is { } attached)
            {
                Assign(attached.Option, attached.Value, token.Source);
                return true;
            }

            return lookup.IsGetoptStyle && CombinedShortFlagsRegex().IsMatch(value) && TryCombined(tokens, ref i, lookup);
        }

        private static string? ValueOf(WizardOption option, CommandToken token) =>
            option.IsRaw() ? token.Source : token.IsLiteral ? token.Value : null;

        private bool TryCombined(IReadOnlyList<CommandToken> tokens, ref int i, OptionLookup lookup)
        {
            var value = tokens[i].Value;
            for (var j = 1; j < value.Length; j++)
            {
                var match = lookup.FindShort(value[j]);
                if (match is null)
                {
                    return false;
                }

                if (match.ChoiceValue is null && match.Option.Type != WizardOptionType.Flag)
                {
                    break;
                }
            }

            for (var j = 1; j < value.Length; j++)
            {
                var match = lookup.FindShort(value[j])!;
                var option = match.Option;
                if (match.ChoiceValue is not null)
                {
                    SetSingle(option, match.ChoiceValue, "-" + value[j]);
                    continue;
                }

                if (option.Type == WizardOptionType.Flag)
                {
                    SetFlag(option, "-" + value[j]);
                    continue;
                }

                var attached = value[(j + 1)..];
                if (attached.Length > 0)
                {
                    Assign(option, attached, "-" + value[j] + attached);
                }
                else if (i + 1 < tokens.Count && !tokens[i + 1].IsRedirection)
                {
                    var next = tokens[++i];
                    Assign(option, ValueOf(option, next), "-" + value[j] + " " + next.Source);
                }
                else
                {
                    Unknown.Add("-" + value[j]);
                }

                break;
            }

            return true;
        }

        private void SetFlag(WizardOption option, string source)
        {
            if (_values.ContainsKey(option.Id))
            {
                Unknown.Add(source);
                return;
            }

            _values[option.Id] = "true";
            Track(option, source);
        }

        private void SetSingle(WizardOption option, string value, string source)
        {
            if (_values.ContainsKey(option.Id))
            {
                Unknown.Add(source);
                return;
            }

            _values[option.Id] = value;
            Track(option, source);
        }

        /// <summary>Assign a value (null = not a literal, keep the source as unknown).</summary>
        private void Assign(WizardOption option, string? value, string source)
        {
            if (value is null)
            {
                Unknown.Add(source);
                return;
            }

            if (option.GetTemplate() is { } template)
            {
                if (_values.Keys.Any(k => k.StartsWith(option.Id + ".", StringComparison.Ordinal))
                    || template.Match(value, option.IsRaw()) is not { } subs)
                {
                    Unknown.Add(source);
                    return;
                }

                foreach (var (name, sub) in subs)
                {
                    _values[option.Id + "." + name] = sub;
                }

                Track(option, source);
                return;
            }

            if (option.IsMultiValued())
            {
                if (!_lists.TryGetValue(option.Id, out var list))
                {
                    _lists[option.Id] = list = [];
                }

                list.Add(option.Type == WizardOptionType.KeyValueList ? WizardEngine.NormalizePair(value, option.KeyValueSeparator) : value);
                Track(option, source);
                return;
            }

            SetSingle(option, value, source);
        }

        private void AssignPositional(WizardOption option, CommandToken token)
        {
            var value = token.IsLiteral ? token.Value : option.IsRaw() ? token.Source : null;
            Assign(option, value, token.Source);
        }

        private void Track(WizardOption option, string source)
        {
            if (!_sources.TryGetValue(option.Id, out var list))
            {
                _sources[option.Id] = list = [];
            }

            list.Add(source);
        }
    }
}
