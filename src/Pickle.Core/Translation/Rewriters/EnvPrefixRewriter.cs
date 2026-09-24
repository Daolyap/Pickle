using System.Text;
using Pickle.Abstractions;

namespace Pickle.Core.Translation.Rewriters;

/// <summary>
/// <c>A=1 B=x cmd args</c> → sets <c>$env:A</c>/<c>$env:B</c> for the statement only and restores the previous values in a
/// <c>finally</c> block. The wrapper is a plain statement (not a script block) so native programs keep the console and
/// dot-sourcing still targets the caller's scope. Prefixes inside a pipeline/chain apply to the whole statement.
/// </summary>
public sealed class EnvPrefixRewriter : IInputRewriter
{
    private readonly bool _isWindows;

    public EnvPrefixRewriter(bool isWindows) => _isWindows = isWindows;

    public string Name => "env-prefix";

    public int Order => 50;

    public RewriteResult? Rewrite(string input, RewriteContext context)
    {
        if (!input.Contains('=', StringComparison.Ordinal))
        {
            return null;
        }

        var edits = new TextEdits();
        foreach (var statement in ShellLexer.Statements(input))
        {
            var assignments = new List<(string Name, string Expression)>();
            var inner = new TextEdits();
            foreach (var segment in statement.Segments)
            {
                var count = 0;
                while (count < segment.Words.Count && ExportRewriter.TryParseAssignment(segment.Words[count], out _, out _))
                {
                    count++;
                }

                if (count == 0 || count == segment.Words.Count)
                {
                    continue;
                }

                for (var i = 0; i < count; i++)
                {
                    ExportRewriter.TryParseAssignment(segment.Words[i], out var name, out var raw);
                    assignments.RemoveAll(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
                    assignments.Add((name, EnvValueTranslator.Translate(raw, name, _isWindows)));
                }

                inner.Replace(segment.Words[0].Start - statement.Start, segment.Words[count].Start - statement.Start, string.Empty);
            }

            if (assignments.Count > 0)
            {
                var body = inner.Apply(input[statement.Start..statement.End]);
                edits.Replace(statement.Start, statement.End, Wrap(assignments, body));
            }
        }

        return edits.Count == 0 ? null : new RewriteResult(edits.Apply(input), "VAR=value before a command sets $env:VAR only while it runs");
    }

    private static string Wrap(List<(string Name, string Expression)> assignments, string body)
    {
        var sb = new StringBuilder();
        foreach (var (name, _) in assignments)
        {
            sb.Append("$__saved_").Append(name).Append(" = $env:").Append(name).Append("; ");
        }

        sb.Append("try { ");
        foreach (var (name, expression) in assignments)
        {
            sb.Append("$env:").Append(name).Append(" = ").Append(expression).Append("; ");
        }

        // $? at the start of finally is the command's status; the trailing if hands it back (a try statement alone always
        // leaves $? true), so the prompt, history and scripts see a failure. Write-Error -ErrorAction Ignore sets $? to
        // false without output or an $Error entry.
        sb.Append(body).Append(" } finally { $__pickle_ok = $?; ");
        foreach (var (name, _) in assignments)
        {
            sb.Append("$env:").Append(name).Append(" = $__saved_").Append(name).Append("; ");
        }

        sb.Append("Remove-Variable ").Append(string.Join(", ", assignments.Select(a => "__saved_" + a.Name))).Append(" }; ");
        sb.Append("if ($__pickle_ok) { Remove-Variable __pickle_ok } else { Remove-Variable __pickle_ok; Write-Error 'failed' -ErrorAction Ignore }");
        return sb.ToString();
    }
}
