using System.Text.RegularExpressions;
using Pickle.Abstractions;

namespace Pickle.Core.Translation.Rewriters;

/// <summary>
/// <c>export A=1 B=$PATH:/x</c> → <c>$env:A = '1'; $env:B = "$env:PATH$([IO.Path]::PathSeparator)/x"</c>,
/// <c>unset A</c> → <c>Remove-Item Env:A</c>, <c>source file</c> → <c>. file</c>.
/// </summary>
public sealed partial class ExportRewriter : IInputRewriter
{
    private readonly bool _isWindows;

    public ExportRewriter(bool isWindows) => _isWindows = isWindows;

    public string Name => "export";

    public int Order => 40;

    public RewriteResult? Rewrite(string input, RewriteContext context)
    {
        var edits = new TextEdits();
        string? explanation = null;
        foreach (var segment in ShellLexer.Segments(input))
        {
            if (segment.Words.Count == 0)
            {
                continue;
            }

            var replacement = segment.Words[0].Literal switch
            {
                "export" => Export(segment),
                "unset" => Unset(segment),
                "source" when segment.Words.Count > 1 => ".",
                _ => null,
            };

            if (replacement is null)
            {
                continue;
            }

            if (segment.Words[0].Literal == "source")
            {
                edits.Replace(segment.Words[0].Start, segment.Words[0].End, replacement);
                explanation ??= "source is . (dot-sourcing) in PowerShell";
            }
            else
            {
                edits.Replace(segment.Start, segment.End, replacement);
                explanation ??= "environment variables are $env:NAME in PowerShell";
            }
        }

        return edits.Count == 0 ? null : new RewriteResult(edits.Apply(input), explanation);
    }

    /// <summary>Splits <c>NAME=value</c> (value is raw source text); false if the word isn't an assignment.</summary>
    public static bool TryParseAssignment(ShellWord word, out string name, out string rawValue)
    {
        name = rawValue = string.Empty;
        var match = AssignmentRegex().Match(word.Text);
        if (!match.Success)
        {
            return false;
        }

        name = match.Groups["name"].Value;
        rawValue = word.Text[match.Length..];
        return true;
    }

    private string? Export(ShellSegment segment)
    {
        var args = segment.Words.Skip(1).ToList();
        if (args.Count == 0 || args is [{ Text: "-p" }])
        {
            return "Get-ChildItem Env:";
        }

        var assignments = new List<(string Name, string Expression)>();
        foreach (var word in args)
        {
            if (TryParseAssignment(word, out var name, out var raw))
            {
                assignments.Add((name, EnvValueTranslator.Translate(raw, name, _isWindows)));
            }
            else if (word.IsPlain && NameRegex().IsMatch(word.Text))
            {
                // `export FOO` exports an existing shell variable.
                assignments.Add((word.Text, "$" + word.Text));
            }
            else
            {
                return null;
            }
        }

        return segment.IsChained
            ? string.Join(" && ", assignments.Select(a => $"Set-Item -LiteralPath Env:{a.Name} -Value {a.Expression}"))
            : string.Join("; ", assignments.Select(a => $"$env:{a.Name} = {a.Expression}"));
    }

    private static string? Unset(ShellSegment segment)
    {
        var drive = "Env:";
        var names = new List<string>();
        foreach (var word in segment.Words.Skip(1))
        {
            switch (word.Literal)
            {
                case "-v":
                    continue;
                case "-f":
                    drive = "Function:";
                    continue;
                case { } literal when NameRegex().IsMatch(literal):
                    names.Add(drive + literal);
                    break;
                default:
                    return null;
            }
        }

        return names.Count == 0 ? null : $"Remove-Item -LiteralPath {string.Join(", ", names)} -ErrorAction Ignore";
    }

    [GeneratedRegex(@"^(?<name>[A-Za-z_][A-Za-z0-9_]*)=")]
    private static partial Regex AssignmentRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex NameRegex();
}
