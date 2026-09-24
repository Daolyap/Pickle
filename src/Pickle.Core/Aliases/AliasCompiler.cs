using System.Text;
using System.Text.RegularExpressions;
using Pickle.Abstractions;
using Pickle.Core.Translation;

namespace Pickle.Core.Aliases;

/// <summary>Turns an <see cref="AliasDefinition"/> into the body of the PowerShell function that implements it.</summary>
public static partial class AliasCompiler
{
    private static readonly HashSet<string> ReservedParameterNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "args", "input", "_", "PSItem", "this", "true", "false", "null", "PSBoundParameters", "MyInvocation", "PSCmdlet",
        "Error", "HOME", "PWD", "PID", "Host", "ExecutionContext", "LASTEXITCODE", "Matches", "PSScriptRoot",
    };

    public sealed record Placeholder(string Name, string? Default);

    /// <summary>Parameterized when the body uses <c>{name}</c>, <c>{name=default}</c> or <c>{*}</c>.</summary>
    public static AliasKind DetectKind(string body) => PlaceholderRegex().IsMatch(body) ? AliasKind.Parameterized : AliasKind.Simple;

    public static IReadOnlyList<Placeholder> Placeholders(string body)
    {
        var result = new List<Placeholder>();
        foreach (Match match in PlaceholderRegex().Matches(body))
        {
            if (match.Groups["name"].Success && !result.Any(p => string.Equals(p.Name, match.Groups["name"].Value, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(new Placeholder(match.Groups["name"].Value, match.Groups["default"].Success ? match.Groups["default"].Value : null));
            }
        }

        return result;
    }

    public static string Usage(AliasDefinition alias)
    {
        var sb = new StringBuilder(alias.Name);
        foreach (var p in Placeholders(alias.Body))
        {
            sb.Append(p.Default is null ? $" <{p.Name}>" : $" [{p.Name}={p.Default}]");
        }

        if (alias.Body.Contains("{*}", StringComparison.Ordinal))
        {
            sb.Append(" [args...]");
        }

        return sb.ToString();
    }

    /// <summary>Throws <see cref="ArgumentException"/> for definitions that can't compile to a sensible function.</summary>
    public static void Validate(AliasDefinition alias)
    {
        AliasNames.Validate(alias.Name);
        if (string.IsNullOrWhiteSpace(alias.Body))
        {
            throw new ArgumentException($"Alias '{alias.Name}' has an empty body.");
        }

        if (alias.Kind == AliasKind.Parameterized)
        {
            var placeholders = Placeholders(alias.Body);
            if (placeholders.Count == 0 && !alias.Body.Contains("{*}", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Parameterized alias '{alias.Name}' has no {{name}}, {{name=default}} or {{*}} placeholders.");
            }

            if (placeholders.FirstOrDefault(p => ReservedParameterNames.Contains(p.Name)) is { } reserved)
            {
                throw new ArgumentException($"'{{{reserved.Name}}}' is a reserved PowerShell variable name; pick another placeholder name.");
            }
        }
    }

    public static string BuildFunction(AliasDefinition alias) =>
        $"function global:{alias.Name} {{\n{Indent(BuildFunctionBody(alias))}\n}}";

    public static string BuildFunctionBody(AliasDefinition alias)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(alias.Description))
        {
            sb.Append("# ").AppendLine(alias.Description.ReplaceLineEndings(" "));
        }

        switch (alias.Kind)
        {
            case AliasKind.Script:
                sb.Append(alias.Body.Trim());
                break;
            case AliasKind.Parameterized:
                AppendParameterized(sb, alias);
                break;
            default:
                sb.Append(GuardRecursion(alias.Name, alias.Body.Trim())).Append(" @args");
                break;
        }

        return sb.ToString();
    }

    private static void AppendParameterized(StringBuilder sb, AliasDefinition alias)
    {
        var placeholders = Placeholders(alias.Body);
        if (placeholders.Count > 0)
        {
            sb.Append("param(")
                .Append(string.Join(", ", placeholders.Select(p => p.Default is null ? "$" + p.Name : $"${p.Name} = {PowerShellText.SingleQuote(p.Default)}")))
                .AppendLine(")");
        }

        var usage = Usage(alias);
        foreach (var p in placeholders.Where(p => p.Default is null))
        {
            var message = $"{alias.Name}: missing required argument <{p.Name}>. Usage: {usage}";
            sb.Append("if (-not $PSBoundParameters.ContainsKey('").Append(p.Name).Append("')) { throw ")
                .Append(PowerShellText.SingleQuote(message)).AppendLine(" }");
        }

        sb.Append(GuardRecursion(alias.Name, SubstitutePlaceholders(alias.Body.Trim())));
    }

    // Each word is rewritten on its own: `{x}` alone → `$x`, compound barewords like `v{x}.txt` → "v$($x).txt",
    // and placeholders inside strings/blocks → `$($x)`.
    private static string SubstitutePlaceholders(string body)
    {
        var edits = new TextEdits();
        foreach (var word in ShellLexer.Words(body))
        {
            if (!PlaceholderRegex().IsMatch(word.Text))
            {
                continue;
            }

            var single = PlaceholderRegex().Match(word.Text);
            if (single.Length == word.Text.Length)
            {
                edits.Replace(word.Start, word.End, single.Groups["rest"].Success ? "@args" : "$" + single.Groups["name"].Value);
                continue;
            }

            var remainder = PlaceholderRegex().Replace(word.Text, string.Empty);
            if (ShellLexer.TryGetLiteral(remainder) == remainder && remainder.IndexOfAny([' ', '\t', '{', '}', '(', ')', '$', '"', '\'', '`']) < 0)
            {
                var sb = new StringBuilder("\"");
                var last = 0;
                foreach (Match m in PlaceholderRegex().Matches(word.Text))
                {
                    sb.Append(PowerShellText.EscapeDoubleQuoted(word.Text[last..m.Index])).Append(Expansion(m));
                    last = m.Index + m.Length;
                }

                sb.Append(PowerShellText.EscapeDoubleQuoted(word.Text[last..])).Append('"');
                edits.Replace(word.Start, word.End, sb.ToString());
            }
            else
            {
                edits.Replace(word.Start, word.End, PlaceholderRegex().Replace(word.Text, Expansion));
            }
        }

        return edits.Apply(body);
    }

    private static string Expansion(Match m) => m.Groups["rest"].Success ? "$($args -join ' ')" : "$($" + m.Groups["name"].Value + ")";

    // `alias ls = ls --color` would call itself forever; call the underlying command instead.
    private static string GuardRecursion(string name, string body)
    {
        var first = ShellLexer.Words(body).FirstOrDefault();
        if (first is null || !string.Equals(first.Literal, name, StringComparison.OrdinalIgnoreCase))
        {
            return body;
        }

        var target = $"& (Get-Command -Name {PowerShellText.SingleQuote(name)} -CommandType Application, Cmdlet, ExternalScript -ErrorAction Stop | Select-Object -First 1)";
        return target + body[first.End..];
    }

    private static string Indent(string text) => string.Join('\n', text.Split('\n').Select(l => "    " + l.TrimEnd('\r')));

    [GeneratedRegex(@"\{(?:(?<rest>\*)|(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:=(?<default>[^{}]*))?)\}")]
    private static partial Regex PlaceholderRegex();
}
