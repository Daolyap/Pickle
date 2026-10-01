using System.Text;
using System.Text.RegularExpressions;

namespace Pickle.Modules.Explain;

public enum PartKind
{
    Command,
    Subcommand,
    Flag,
    Value,
    Argument,
    Operator,
    Redirect,
    Assignment,
    Note,
}

/// <summary>One piece of the line and what it does.</summary>
public sealed record ExplainedPart(string Text, string Meaning, PartKind Kind, int Depth = 0);

public sealed record Explanation(string Line, IReadOnlyList<ExplainedPart> Parts, IReadOnlyList<string> Warnings)
{
    public string Summary => Parts.FirstOrDefault(p => p.Kind == PartKind.Command)?.Meaning ?? "Nothing to explain.";
}

/// <summary>What the shell knows about a name that is not in the pack: an alias, cmdlet or function, with its parameters.</summary>
public sealed record ShellCommandInfo(string Kind, string? ResolvedName, string? Module, IReadOnlyDictionary<string, string> Parameters);

/// <summary>
/// Explains a command line without running or even locating anything: it splits the line like a shell would (pipes,
/// <c>&amp;&amp;</c>, redirections, quotes, <c>$(…)</c>), looks the command, its subcommand and each flag up in the
/// <see cref="ExplainPack"/>, and, for PowerShell names the pack does not know, asks the shell for the alias or cmdlet
/// (<paramref name="describeShellCommand"/>). Unknown programs are never run with --help.
/// </summary>
public sealed partial class CommandExplainer(ExplainPack pack, Func<string, CancellationToken, Task<ShellCommandInfo?>>? describeShellCommand = null)
{
    private static readonly Dictionary<string, string> Operators = new(StringComparer.Ordinal)
    {
        ["|"] = "pipe: the output of the command on the left becomes the input of the one on the right",
        ["||"] = "run the next command only if the previous one failed",
        ["&&"] = "run the next command only if the previous one succeeded",
        [";"] = "then run the next command (whatever happened before)",
        ["&"] = "run the previous command in the background",
    };

    private static readonly Dictionary<string, string> Redirections = new(StringComparer.Ordinal)
    {
        [">"] = "write the output to this file, replacing what is there",
        [">>"] = "append the output to this file",
        ["<"] = "read the input from this file",
        ["2>"] = "write error messages to this file",
        ["2>>"] = "append error messages to this file",
        ["&>"] = "write output and errors to this file",
        ["1>"] = "write the output to this file",
        ["2>&1"] = "send error messages to the same place as the normal output",
        ["1>&2"] = "send the normal output to where errors go",
        [">&2"] = "send the normal output to where errors go",
    };

    private static readonly (string Pattern, string Message)[] GlobalWarnings =
    [
        (@"(curl|wget)\b[^|]*\|\s*(sudo\s+)?(ba|z|da)?sh\b", "Piping a download straight into a shell runs code you have not read."),
        (@":\(\)\s*\{\s*:\s*\|\s*:&\s*\}\s*;\s*:", "This is a fork bomb: it starts processes until the machine stops responding."),
        (@">\s*/dev/(sd|nvme|hd|disk)", "Writing to a disk device overwrites it."),
        (@"\brm\b[^;|&]*\s(-[a-zA-Z]*r[a-zA-Z]*f|-[a-zA-Z]*f[a-zA-Z]*r)[^;|&]*\s(/|/\*|~|~/\*|\$HOME)(\s|$)", "This deletes recursively from the root or your home folder."),
        (@"\bchmod\b[^;|&]*-R[^;|&]*\b777\b", "chmod -R 777 makes everything world-writable."),
        (@"\bRemove-Item\b[^;|&]*-Recurse[^;|&]*-Force[^;|&]*(\s|^)(/|\\|[A-Za-z]:\\?)(\s|$)", "This force-deletes a whole drive or the root."),
        (@"\bFormat-Volume\b|\bClear-Disk\b", "This erases a disk or volume."),
        (@"\bgit\s+push\b[^;|&]*\s(--force|-f)(\s|$)", "A plain force push can overwrite other people's commits; --force-with-lease is safer."),
    ];

    public async Task<Explanation> ExplainAsync(string line, CancellationToken cancellationToken = default)
    {
        var parts = new List<ExplainedPart>();
        var warnings = new List<string>();
        var text = line.Trim();
        if (text.Length == 0)
        {
            return new Explanation(line, parts, warnings);
        }

        foreach (var item in Split(text))
        {
            if (item.IsOperator)
            {
                parts.Add(new ExplainedPart(item.Text, Operators[item.Text], PartKind.Operator));
            }
            else
            {
                await ExplainSegmentAsync(item.Words, parts, warnings, 0, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var (pattern, message) in GlobalWarnings)
        {
            if (Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)) && !warnings.Contains(message))
            {
                warnings.Add(message);
            }
        }

        return new Explanation(line, parts, warnings);
    }

    public sealed record Word(string Text, string Value, bool Quoted);

    public sealed record Piece(string Text, bool IsOperator, IReadOnlyList<Word> Words);

    /// <summary>Splits a line into command segments and the operators between them, honoring quotes, escapes and nesting of ( ), { } and [ ].</summary>
    public static IReadOnlyList<Piece> Split(string line)
    {
        var pieces = new List<Piece>();
        var words = new List<Word>();
        var current = new StringBuilder();
        var raw = new StringBuilder();
        var quoted = false;
        var depth = 0;
        char? quote = null;

        void EndWord()
        {
            if (raw.Length > 0 || quoted)
            {
                words.Add(new Word(raw.ToString(), current.ToString(), quoted));
            }

            current.Clear();
            raw.Clear();
            quoted = false;
        }

        void EndSegment(string? op)
        {
            EndWord();
            if (words.Count > 0)
            {
                pieces.Add(new Piece(string.Join(' ', words.Select(w => w.Text)), false, [.. words]));
                words.Clear();
            }

            if (op is not null)
            {
                pieces.Add(new Piece(op, true, []));
            }
        }

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote is { } q)
            {
                raw.Append(c);
                if (c == '\\' && q == '"' && i + 1 < line.Length && line[i + 1] is '"' or '\\' or '$' or '`')
                {
                    raw.Append(line[++i]);
                    current.Append(line[i]);
                }
                else if (c == q)
                {
                    quote = null;
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
                quoted = true;
                raw.Append(c);
                continue;
            }

            // A backslash only escapes punctuation and spaces, so Windows paths (C:\src) keep theirs.
            if ((c == '`' || (c == '\\' && !char.IsLetterOrDigit(line[Math.Min(i + 1, line.Length - 1)]))) && i + 1 < line.Length)
            {
                raw.Append(c).Append(line[i + 1]);
                current.Append(line[++i]);
                continue;
            }

            if (c is '(' or '{' or '[')
            {
                depth++;
            }
            else if (c is ')' or '}' or ']' && depth > 0)
            {
                depth--;
            }

            if (depth == 0)
            {
                if (char.IsWhiteSpace(c))
                {
                    EndWord();
                    continue;
                }

                if (c is '|' or '&' or ';')
                {
                    var next = i + 1 < line.Length ? line[i + 1] : '\0';
                    var previous = raw.Length > 0 ? raw[^1] : '\0';
                    if (c == '&' && (next == '>' || previous is '>' or '<'))
                    {
                        raw.Append(c);
                        current.Append(c);
                        continue;
                    }

                    var op = c == next && c != ';' ? new string(c, 2) : c.ToString();
                    EndSegment(op);
                    i += op.Length - 1;
                    continue;
                }
            }

            raw.Append(c);
            current.Append(c);
        }

        EndSegment(null);
        return pieces;
    }

    private async Task ExplainSegmentAsync(IReadOnlyList<Word> words, List<ExplainedPart> parts, List<string> warnings, int depth, CancellationToken cancellationToken)
    {
        var index = 0;
        while (index < words.Count && Assignment().IsMatch(words[index].Text) && !words[index].Quoted)
        {
            var eq = words[index].Text.IndexOf('=', StringComparison.Ordinal);
            parts.Add(new ExplainedPart(words[index].Text, $"set {words[index].Text[..eq]} for this command only", PartKind.Assignment, depth));
            index++;
        }

        // Redirections can come anywhere; pull them out first.
        var arguments = new List<Word>();
        for (var i = index; i < words.Count; i++)
        {
            var w = words[i];
            if (!w.Quoted && Redirections.TryGetValue(w.Text, out var redirect))
            {
                parts.Add(new ExplainedPart(w.Text + (redirect.Contains("this file", StringComparison.Ordinal) && i + 1 < words.Count ? " " + words[i + 1].Text : string.Empty), redirect.Replace("this file", i + 1 < words.Count ? words[i + 1].Value : "a file", StringComparison.Ordinal), PartKind.Redirect, depth));
                if (redirect.Contains("this file", StringComparison.Ordinal))
                {
                    i++;
                }

                continue;
            }

            if (!w.Quoted && RedirectGlued().Match(w.Text) is { Success: true } glued)
            {
                var op = glued.Groups[1].Value;
                parts.Add(new ExplainedPart(w.Text, Redirections[op].Replace("this file", glued.Groups[2].Value, StringComparison.Ordinal), PartKind.Redirect, depth));
                continue;
            }

            arguments.Add(w);
        }

        if (arguments.Count == 0)
        {
            return;
        }

        var name = arguments[0].Value;
        var entry = pack.Find(name);
        var text = string.Join(' ', words.Skip(index).Select(w => w.Text));
        if (entry is null)
        {
            await ExplainUnknownAsync(arguments, parts, depth, cancellationToken).ConfigureAwait(false);
            return;
        }

        parts.Add(new ExplainedPart(arguments[0].Text, entry.Summary, PartKind.Command, depth));
        AddWarnings(entry, text, warnings);
        var flags = new Dictionary<string, string>(entry.Flags, StringComparer.Ordinal);
        var current = entry;
        var positional = 0;
        var afterDashDash = false;
        var wrapped = current.Wraps;
        for (var i = 1; i < arguments.Count; i++)
        {
            var word = arguments[i];
            var value = word.Value;
            if (word.Quoted is false && CommandSubstitution().IsMatch(value))
            {
                parts.Add(new ExplainedPart(word.Text, "runs the command inside and substitutes its output here", PartKind.Argument, depth + 1));
                continue;
            }

            if (!afterDashDash && value == "--" && !word.Quoted)
            {
                afterDashDash = true;
                parts.Add(new ExplainedPart("--", flags.TryGetValue("--", out var m) ? m.TrimStart('=') : "end of options: everything after this is an argument, not a flag", PartKind.Flag, depth + 1));
                if (wrapped)
                {
                    await ExplainSegmentAsync(arguments.Skip(i + 1).ToList(), parts, warnings, depth + 1, cancellationToken).ConfigureAwait(false);
                    return;
                }

                continue;
            }

            if (!afterDashDash && !word.Quoted && value.Length > 1 && value[0] == '-' && !char.IsDigit(value[1]))
            {
                i = ExplainFlag(arguments, i, flags, current, name, parts, depth + 1);
                continue;
            }

            if (!afterDashDash && wrapped && positional == 0 && !word.Quoted)
            {
                await ExplainSegmentAsync(arguments.Skip(i).ToList(), parts, warnings, depth + 1, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (positional == 0 && current.Subcommands.Count > 0 && current.Subcommands.TryGetValue(value, out var sub))
            {
                parts.Add(new ExplainedPart(word.Text, sub.Summary, PartKind.Subcommand, depth + 1));
                AddWarnings(sub, text, warnings);
                foreach (var (flag, meaning) in sub.Flags)
                {
                    flags[flag] = meaning;
                }

                current = sub;
                positional = 0;
                continue;
            }

            if (!afterDashDash && flags.TryGetValue(value, out var keyword) && !value.StartsWith('-'))
            {
                parts.Add(new ExplainedPart(word.Text, keyword.TrimStart('='), PartKind.Flag, depth + 1));
                continue;
            }

            if (!afterDashDash && value.IndexOf('=', StringComparison.Ordinal) is var eq and > 0 && flags.TryGetValue(value[..eq], out var pair))
            {
                parts.Add(new ExplainedPart(word.Text, pair.TrimStart('=') + ": " + value[(eq + 1)..], PartKind.Flag, depth + 1));
                continue;
            }

            parts.Add(new ExplainedPart(word.Text, DescribeArgument(name, current, value, positional), PartKind.Argument, depth + 1));
            positional++;
        }
    }

    private static int ExplainFlag(IReadOnlyList<Word> arguments, int i, Dictionary<string, string> flags, CommandEntry entry, string command, List<ExplainedPart> parts, int depth)
    {
        var token = arguments[i].Value;
        var eq = token.IndexOf('=', StringComparison.Ordinal);
        var name = token.StartsWith("--", StringComparison.Ordinal) && eq > 0 ? token[..eq] : token;
        var inline = token.StartsWith("--", StringComparison.Ordinal) && eq > 0 ? token[(eq + 1)..] : null;

        if (flags.TryGetValue(name, out var exact))
        {
            var takesValue = exact.StartsWith('=');
            var meaning = exact.TrimStart('=');
            if (inline is not null)
            {
                parts.Add(new ExplainedPart(token, $"{meaning}: {inline}", PartKind.Flag, depth));
                return i;
            }

            if (takesValue && i + 1 < arguments.Count)
            {
                parts.Add(new ExplainedPart($"{token} {arguments[i + 1].Text}", $"{meaning}: {arguments[i + 1].Value}", PartKind.Flag, depth));
                return i + 1;
            }

            parts.Add(new ExplainedPart(token, meaning, PartKind.Flag, depth));
            return i;
        }

        if (!token.StartsWith("--", StringComparison.Ordinal))
        {
            // -n5 / -A3: a value flag with its value attached.
            if (token.Length > 2 && flags.TryGetValue(token[..2], out var attached) && attached.StartsWith('='))
            {
                parts.Add(new ExplainedPart(token, $"{attached.TrimStart('=')}: {token[2..]}", PartKind.Flag, depth));
                return i;
            }

            // -xzvf: every letter is a flag; the last may take the next word.
            if (entry.CombineShortFlags && token.Length > 2 && token[1..].All(c => flags.ContainsKey("-" + c)))
            {
                var letters = token[1..];
                var consumed = i;
                for (var k = 0; k < letters.Length; k++)
                {
                    var meaning = flags["-" + letters[k]];
                    var takesValue = meaning.StartsWith('=');
                    if (takesValue && k == letters.Length - 1 && i + 1 < arguments.Count)
                    {
                        parts.Add(new ExplainedPart($"-{letters[k]} {arguments[i + 1].Text}", $"{meaning.TrimStart('=')}: {arguments[i + 1].Value}", PartKind.Flag, depth));
                        consumed = i + 1;
                    }
                    else
                    {
                        parts.Add(new ExplainedPart("-" + letters[k], meaning.TrimStart('='), PartKind.Flag, depth));
                    }
                }

                return consumed;
            }
        }

        parts.Add(new ExplainedPart(token, entry.Subcommands.Count == 0 && flags.Count == 0 ? "an option (the pack has no details for this command)" : $"an option of {command} that the pack does not describe (run '{command} --help')", PartKind.Flag, depth));
        return i;
    }

    private async Task ExplainUnknownAsync(IReadOnlyList<Word> arguments, List<ExplainedPart> parts, int depth, CancellationToken cancellationToken)
    {
        var name = arguments[0];
        ShellCommandInfo? info = null;
        if (describeShellCommand is not null && ShellName().IsMatch(name.Value))
        {
            try
            {
                info = await describeShellCommand(name.Value, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Management.Automation.RuntimeException)
            {
            }
        }

        if (info is null)
        {
            parts.Add(new ExplainedPart(name.Text, "a program or script (not in the explainer's knowledge; Pickle never runs unknown programs just to explain them)", PartKind.Command, depth));
            parts.AddRange(arguments.Skip(1).Select(a => new ExplainedPart(a.Text, a.Value.StartsWith('-') && a.Value.Length > 1 ? "an option" : "an argument", a.Value.StartsWith('-') && a.Value.Length > 1 ? PartKind.Flag : PartKind.Argument, depth + 1)));
            return;
        }

        var resolved = info.ResolvedName ?? name.Value;
        var known = pack.Find(resolved);
        var kind = info.Kind switch
        {
            "Alias" => $"alias for {resolved}" + (info.Module is { Length: > 0 } m ? $" ({m})" : string.Empty),
            "Cmdlet" => $"cmdlet{(info.Module is { Length: > 0 } cm ? " from " + cm : string.Empty)}",
            "Function" => "PowerShell function" + (info.Module is { Length: > 0 } fm ? " from " + fm : string.Empty),
            "Application" => "a program on your PATH",
            _ => info.Kind,
        };
        parts.Add(new ExplainedPart(name.Text, known?.Summary is { Length: > 0 } s ? $"{kind}: {s}" : kind, PartKind.Command, depth));
        for (var i = 1; i < arguments.Count; i++)
        {
            var arg = arguments[i];
            if (arg.Value.StartsWith('-') && arg.Value.Length > 1 && !char.IsDigit(arg.Value[1]))
            {
                var parameter = info.Parameters.Keys.FirstOrDefault(k => k.StartsWith(arg.Value.TrimStart('-'), StringComparison.OrdinalIgnoreCase));
                var type = parameter is null ? null : info.Parameters[parameter];
                var isSwitch = type == "SwitchParameter";
                if (!isSwitch && parameter is not null && i + 1 < arguments.Count && !arguments[i + 1].Value.StartsWith('-'))
                {
                    parts.Add(new ExplainedPart($"{arg.Text} {arguments[i + 1].Text}", $"parameter {parameter} ({type}): {arguments[i + 1].Value}", PartKind.Flag, depth + 1));
                    i++;
                }
                else
                {
                    parts.Add(new ExplainedPart(arg.Text, parameter is null ? "a parameter" : $"parameter {parameter}" + (isSwitch ? " (switch: on/off)" : $" ({type})"), PartKind.Flag, depth + 1));
                }
            }
            else
            {
                parts.Add(new ExplainedPart(arg.Text, DescribeArgument(resolved, known ?? new CommandEntry(), arg.Value, i - 1), PartKind.Argument, depth + 1));
            }
        }
    }

    private static string DescribeArgument(string command, CommandEntry entry, string value, int position)
    {
        if (command.Equals("chmod", StringComparison.OrdinalIgnoreCase) && position == 0 && DescribeMode(value) is { } mode)
        {
            return mode;
        }

        if (position == 0 && entry.Args is { Length: > 0 } args)
        {
            return args;
        }

        if (UrlLike().IsMatch(value))
        {
            return "a web address";
        }

        if (value.Contains('*', StringComparison.Ordinal) || value.Contains('?', StringComparison.Ordinal))
        {
            return "a wildcard pattern: the shell expands it to every matching name";
        }

        if (value.StartsWith('~') || value.StartsWith('/') || value.StartsWith("./", StringComparison.Ordinal) || value.StartsWith("../", StringComparison.Ordinal) || WindowsPath().IsMatch(value))
        {
            return value switch { "/" => "the root of the filesystem", "~" => "your home folder", "." => "the current folder", ".." => "the parent folder", _ => "a path" };
        }

        return value switch { "." => "the current folder", ".." => "the parent folder", _ => position == 0 ? "an argument" : "another argument" };
    }

    /// <summary><c>755</c> → "owner can read, write and run; group can read and run; others can read and run (rwxr-xr-x)"; symbolic modes are described too.</summary>
    public static string? DescribeMode(string mode)
    {
        if (OctalMode().IsMatch(mode))
        {
            var digits = mode.PadLeft(3, '0')[^3..];
            string Who(char d)
            {
                var v = d - '0';
                var allowed = new List<string>();
                if ((v & 4) != 0)
                {
                    allowed.Add("read");
                }

                if ((v & 2) != 0)
                {
                    allowed.Add("write");
                }

                if ((v & 1) != 0)
                {
                    allowed.Add("run");
                }

                return allowed.Count switch { 0 => "nothing", 1 => allowed[0], 2 => $"{allowed[0]} and {allowed[1]}", _ => "read, write and run" };
            }

            string Symbolic(char d)
            {
                var v = d - '0';
                return $"{((v & 4) != 0 ? 'r' : '-')}{((v & 2) != 0 ? 'w' : '-')}{((v & 1) != 0 ? 'x' : '-')}";
            }

            return $"permissions {mode}: owner can {Who(digits[0])}; group can {Who(digits[1])}; others can {Who(digits[2])} ({Symbolic(digits[0])}{Symbolic(digits[1])}{Symbolic(digits[2])})";
        }

        if (SymbolicMode().Match(mode) is { Success: true } m)
        {
            var who = m.Groups[1].Value.Length == 0 ? "everyone (subject to umask)" : string.Join(", ", m.Groups[1].Value.Select(c => c switch { 'u' => "owner", 'g' => "group", 'o' => "others", _ => "everyone" }));
            var op = m.Groups[2].Value switch { "+" => "gets", "-" => "loses", _ => "is set to exactly" };
            var what = string.Join(", ", m.Groups[3].Value.Select(c => c switch { 'r' => "read", 'w' => "write", 'x' => "run", 'X' => "run (directories and already-runnable files)", 's' => "set-id", 't' => "sticky bit", _ => c.ToString() }));
            return $"{who} {op} {what} permission";
        }

        return null;
    }

    private static void AddWarnings(CommandEntry entry, string text, List<string> warnings)
    {
        foreach (var rule in entry.Warnings)
        {
            try
            {
                if (Regex.IsMatch(text, rule.Pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)) && !warnings.Contains(rule.Message))
                {
                    warnings.Add(rule.Message);
                }
            }
            catch (RegexMatchTimeoutException)
            {
            }
            catch (ArgumentException)
            {
            }
        }
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*=")]
    private static partial Regex Assignment();

    [GeneratedRegex(@"^(2>>|2>|&>|1>|>>|>|<)(.+)$")]
    private static partial Regex RedirectGlued();

    [GeneratedRegex(@"\$\(|`")]
    private static partial Regex CommandSubstitution();

    [GeneratedRegex(@"^[A-Za-z]+-[A-Za-z]+$|^[A-Za-z][A-Za-z0-9_.-]*$")]
    private static partial Regex ShellName();

    [GeneratedRegex(@"^[a-z][a-z0-9+.-]*://", RegexOptions.IgnoreCase)]
    private static partial Regex UrlLike();

    [GeneratedRegex(@"^[A-Za-z]:[\\/]")]
    private static partial Regex WindowsPath();

    [GeneratedRegex(@"^[0-7]{3,4}$")]
    private static partial Regex OctalMode();

    [GeneratedRegex(@"^([ugoa]*)([-+=])([rwxXst]+)$")]
    private static partial Regex SymbolicMode();
}
