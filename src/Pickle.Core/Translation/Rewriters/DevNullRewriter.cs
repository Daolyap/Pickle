using System.Text.RegularExpressions;
using Pickle.Abstractions;

namespace Pickle.Core.Translation.Rewriters;

/// <summary><c>&gt;/dev/null</c>, <c>2&gt;/dev/null</c>, <c>&amp;&gt;/dev/null</c>, <c>&gt;/dev/null 2&gt;&amp;1</c> → <c>$null</c> redirections.</summary>
public sealed partial class DevNullRewriter : IInputRewriter
{
    public string Name => "devnull";

    public int Order => 20;

    public RewriteResult? Rewrite(string input, RewriteContext context)
    {
        if (!input.Contains("/dev/null", StringComparison.Ordinal))
        {
            return null;
        }

        var edits = new TextEdits();
        foreach (var segment in ShellLexer.Segments(input))
        {
            RewriteSegment(segment, edits);
        }

        return edits.Count == 0 ? null : new RewriteResult(edits.Apply(input), "/dev/null is $null in PowerShell");
    }

    private static void RewriteSegment(ShellSegment segment, TextEdits edits)
    {
        var words = segment.Words;
        var pending = new List<(int Start, int End, string Text)>();
        var stdoutIndex = -1;
        var previousEnd = segment.Start;
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            if (!word.IsPlain)
            {
                previousEnd = word.End;
                continue;
            }

            var inline = InlineRegex().Match(word.Text);
            if (inline.Success)
            {
                var target = Map(inline.Groups["op"].Value);
                if (target == ">$null")
                {
                    stdoutIndex = pending.Count;
                }

                pending.Add((word.Start, word.End, target));
            }
            else if (OperatorRegex().IsMatch(word.Text) && i + 1 < words.Count && words[i + 1].Text == "/dev/null")
            {
                var target = Map(word.Text);
                if (target == ">$null")
                {
                    stdoutIndex = pending.Count;
                }

                pending.Add((word.Start, words[i + 1].End, target));
                i++;
                word = words[i];
            }
            else if (word.Text == "2>&1" && stdoutIndex >= 0)
            {
                // bash: `>/dev/null 2>&1` silences both streams.
                var (start, end, _) = pending[stdoutIndex];
                pending[stdoutIndex] = (start, end, "*>$null");
                pending.Add((previousEnd, word.End, string.Empty));
                stdoutIndex = -1;
            }

            previousEnd = word.End;
        }

        foreach (var (start, end, text) in pending)
        {
            edits.Replace(start, end, text);
        }
    }

    private static string Map(string op) => op switch
    {
        ">" or ">>" or "1>" or "1>>" => ">$null",
        "&>" or "&>>" or "*>" or "*>>" or ">&" => "*>$null",
        _ => op[0] + ">$null",
    };

    [GeneratedRegex(@"^(?<op>[1-6*&]?>>?|>&)/dev/null$")]
    private static partial Regex InlineRegex();

    [GeneratedRegex(@"^(?:[1-6*&]?>>?|>&)$")]
    private static partial Regex OperatorRegex();
}
