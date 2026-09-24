using System.Management.Automation.Language;
using System.Text;
using System.Text.RegularExpressions;

namespace Pickle.Core.Translation;

/// <summary>Quoting helpers for generating PowerShell source and Windows command lines.</summary>
public static partial class PowerShellText
{
    public static string SingleQuote(string value) => "'" + CodeGeneration.EscapeSingleQuotedStringContent(value) + "'";

    /// <summary>Leaves simple words (package ids, paths without spaces) bare, single-quotes everything else.</summary>
    public static string QuoteIfNeeded(string value) => BarewordRegex().IsMatch(value) ? value : SingleQuote(value);

    /// <summary>Escapes literal text for use inside a PowerShell double-quoted string.</summary>
    public static string EscapeDoubleQuoted(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '`' or '$' || ShellLexer.IsDoubleQuote(c))
            {
                sb.Append('`');
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>Quotes one argument so CommandLineToArgvW (and .NET's argv parser) reads it back unchanged.</summary>
    public static string WindowsArgument(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            return arg;
        }

        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                sb.Append('\\', (backslashes * 2) + 1).Append('"');
            }
            else
            {
                sb.Append('\\', backslashes).Append(c);
            }

            backslashes = 0;
        }

        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    [GeneratedRegex(@"^[A-Za-z0-9._+/\\~][A-Za-z0-9._+/\\~:-]*$")]
    private static partial Regex BarewordRegex();
}

/// <summary>
/// Translates the value side of a POSIX assignment (<c>FOO=value</c>) into a PowerShell expression:
/// <c>$VAR</c> → <c>$env:VAR</c> (PowerShell automatic variables like <c>$HOME</c> stay), leading <c>~</c> → <c>$HOME</c>,
/// and on Windows <c>:</c>-separated PATH lists → <c>[IO.Path]::PathSeparator</c>.
/// </summary>
public static partial class EnvValueTranslator
{
    private static readonly HashSet<string> PowerShellVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "HOME", "PWD", "PID", "HOST", "PROFILE", "PSHOME", "true", "false", "null", "args", "input", "_", "PSItem",
        "this", "LASTEXITCODE", "Error", "ExecutionContext", "MyInvocation", "PSScriptRoot", "PSCommandPath",
        "IsWindows", "IsLinux", "IsMacOS", "IsCoreCLR", "PSVersionTable", "PSEdition", "ShellId", "Matches", "OFS",
        "PickleHome", "PickleVersion",
    };

    private enum PartKind
    {
        Literal,
        EnvVar,
        PsVar,
        Raw,
        Separator,
    }

    private readonly record struct Part(PartKind Kind, string Text);

    public static string Translate(string raw, string variableName, bool isWindows)
    {
        var parts = Parse(raw);
        if (isWindows && variableName.EndsWith("PATH", StringComparison.OrdinalIgnoreCase))
        {
            parts = SplitPathList(parts);
        }

        if (parts.All(p => p.Kind == PartKind.Literal))
        {
            return PowerShellText.SingleQuote(string.Concat(parts.Select(p => p.Text)));
        }

        if (parts.Count == 1)
        {
            return Render(parts[0], null);
        }

        var sb = new StringBuilder("\"");
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            sb.Append(part.Kind == PartKind.Literal
                ? PowerShellText.EscapeDoubleQuoted(part.Text)
                : Render(part, i + 1 < parts.Count ? parts[i + 1] : null));
        }

        return sb.Append('"').ToString();
    }

    private static string Render(Part part, Part? next)
    {
        var needsBraces = next is { Kind: PartKind.Literal } n && n.Text.Length > 0 && (char.IsLetterOrDigit(n.Text[0]) || n.Text[0] is '_' or ':' or '?');
        return part.Kind switch
        {
            PartKind.EnvVar => needsBraces ? "${env:" + part.Text + "}" : "$env:" + part.Text,
            PartKind.PsVar => needsBraces ? "${" + part.Text + "}" : "$" + part.Text,
            PartKind.Separator => "$([IO.Path]::PathSeparator)",
            PartKind.Raw => part.Text,
            _ => PowerShellText.SingleQuote(part.Text),
        };
    }

    private static List<Part> Parse(string raw)
    {
        var parts = new List<Part>();
        var literal = new StringBuilder();
        var itemStart = true;

        void Flush()
        {
            if (literal.Length > 0)
            {
                parts.Add(new Part(PartKind.Literal, literal.ToString()));
                literal.Clear();
            }
        }

        void Add(Part part)
        {
            Flush();
            parts.Add(part);
        }

        var i = 0;
        while (i < raw.Length)
        {
            var c = raw[i];
            if (ShellLexer.IsSingleQuote(c))
            {
                var end = ShellLexer.SkipSingleQuoted(raw, i);
                var inner = raw[(i + 1)..Math.Max(i + 1, end - 1)];
                literal.Append(inner.Replace("''", "'", StringComparison.Ordinal));
                i = end;
                itemStart = false;
                continue;
            }

            if (ShellLexer.IsDoubleQuote(c))
            {
                var end = ShellLexer.SkipDoubleQuoted(raw, i);
                var j = i + 1;
                var close = end - 1;
                while (j < close)
                {
                    var d = raw[j];
                    if (d == '`' && j + 1 < close)
                    {
                        literal.Append(raw[j + 1]);
                        j += 2;
                    }
                    else if (d == '\\' && j + 1 < close && raw[j + 1] is '$' or '"')
                    {
                        literal.Append(raw[j + 1]);
                        j += 2;
                    }
                    else if (d == '$' && TryParseVariable(raw, j, close, out var part, out var next))
                    {
                        Add(part);
                        j = next;
                    }
                    else
                    {
                        literal.Append(d);
                        j++;
                    }
                }

                i = end;
                itemStart = false;
                continue;
            }

            if (c == '`' && i + 1 < raw.Length)
            {
                literal.Append(raw[i + 1]);
                i += 2;
                itemStart = false;
                continue;
            }

            if (c == '\\' && i + 1 < raw.Length && raw[i + 1] == '$')
            {
                literal.Append('$');
                i += 2;
                itemStart = false;
                continue;
            }

            if (c == '$' && TryParseVariable(raw, i, raw.Length, out var variable, out var after))
            {
                Add(variable);
                i = after;
                itemStart = false;
                continue;
            }

            if (c == '~' && itemStart && (i + 1 == raw.Length || raw[i + 1] is '/' or '\\' or ':'))
            {
                Add(new Part(PartKind.PsVar, "HOME"));
                i++;
                itemStart = false;
                continue;
            }

            literal.Append(c);
            itemStart = c == ':';
            i++;
        }

        Flush();
        if (parts.Count == 0)
        {
            parts.Add(new Part(PartKind.Literal, string.Empty));
        }

        return parts;
    }

    private static bool TryParseVariable(string s, int dollar, int limit, out Part part, out int next)
    {
        part = default;
        next = dollar;
        if (dollar + 1 >= limit)
        {
            return false;
        }

        var c = s[dollar + 1];
        if (c == '(')
        {
            next = Math.Min(limit, ShellLexer.SkipBalanced(s, dollar + 1));
            part = new Part(PartKind.Raw, s[dollar..next]);
            return true;
        }

        if (c == '{')
        {
            var close = s.IndexOf('}', dollar + 2);
            if (close < 0 || close >= limit)
            {
                return false;
            }

            var name = s[(dollar + 2)..close];
            next = close + 1;
            part = name.Contains(':', StringComparison.Ordinal) ? new Part(PartKind.Raw, s[dollar..next]) : Classify(name);
            return true;
        }

        var match = VariableRegex().Match(s, dollar + 1, limit - dollar - 1);
        if (!match.Success || match.Index != dollar + 1)
        {
            return false;
        }

        next = match.Index + match.Length;
        part = match.Groups["scope"].Success ? new Part(PartKind.Raw, s[dollar..next]) : Classify(match.Groups["name"].Value);
        return true;
    }

    private static Part Classify(string name) =>
        PowerShellVariables.Contains(name) ? new Part(PartKind.PsVar, name) : new Part(PartKind.EnvVar, name);

    private static List<Part> SplitPathList(List<Part> parts)
    {
        var result = new List<Part>();
        foreach (var part in parts)
        {
            if (part.Kind != PartKind.Literal || !part.Text.Contains(':', StringComparison.Ordinal))
            {
                result.Add(part);
                continue;
            }

            var text = part.Text;
            var itemStart = 0;
            var atItemStart = result.Count == 0 || result[^1].Kind == PartKind.Separator;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] != ':')
                {
                    continue;
                }

                var item = text[itemStart..i];
                var isDrive = atItemStart && item.Length == 1 && char.IsAsciiLetter(item[0]) && i + 1 < text.Length && text[i + 1] is '\\' or '/';
                var isUrl = i + 2 < text.Length && text[i + 1] == '/' && text[i + 2] == '/';
                if (isDrive || isUrl)
                {
                    continue;
                }

                if (item.Length > 0)
                {
                    result.Add(new Part(PartKind.Literal, item));
                }

                result.Add(new Part(PartKind.Separator, string.Empty));
                itemStart = i + 1;
                atItemStart = true;
            }

            if (itemStart < text.Length)
            {
                result.Add(new Part(PartKind.Literal, text[itemStart..]));
            }
        }

        return result;
    }

    [GeneratedRegex(@"(?:(?<scope>(?i:env|global|script|local|private|using|variable)):)?(?<name>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex VariableRegex();
}
