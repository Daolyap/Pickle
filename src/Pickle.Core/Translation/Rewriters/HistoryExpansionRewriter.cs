using Pickle.Abstractions;

namespace Pickle.Core.Translation.Rewriters;

/// <summary><c>!!</c> → previous command, <c>!$</c> → its last argument (standalone words outside quotes only, so <c>!!$x</c> stays PowerShell).</summary>
public sealed class HistoryExpansionRewriter : IInputRewriter
{
    private const int MaxDepth = 10;

    public string Name => "history";

    public int Order => 10;

    public RewriteResult? Rewrite(string input, RewriteContext context)
    {
        if (!input.Contains('!', StringComparison.Ordinal))
        {
            return null;
        }

        var designators = ShellLexer.Words(input).Where(IsDesignator).ToList();
        if (designators.Count == 0)
        {
            return null;
        }

        var previous = Resolve(context.RecentHistory, context.RecentHistory.Count - 1, 0);
        if (previous is null)
        {
            return null;
        }

        var rewritten = Expand(input, designators, previous);
        var explanation = designators.Any(w => w.Text == "!!")
            ? "!! is the previous command"
            : "!$ is the last argument of the previous command";
        return new RewriteResult(rewritten, explanation);
    }

    private static bool IsDesignator(ShellWord word) => word.Text is "!!" or "!$";

    private static string Expand(string line, List<ShellWord> designators, string previous)
    {
        var edits = new TextEdits();
        foreach (var word in designators)
        {
            edits.Replace(word.Start, word.End, word.Text == "!!" ? previous : LastArgument(previous));
        }

        return edits.Apply(line);
    }

    // History stores lines as typed, so an earlier entry may itself contain !! and must be expanded first.
    private static string? Resolve(IReadOnlyList<string> history, int index, int depth)
    {
        while (index >= 0 && string.IsNullOrWhiteSpace(history[index]))
        {
            index--;
        }

        if (index < 0)
        {
            return null;
        }

        var line = history[index].Trim();
        if (depth >= MaxDepth || !line.Contains('!', StringComparison.Ordinal))
        {
            return line;
        }

        var designators = ShellLexer.Words(line).Where(IsDesignator).ToList();
        if (designators.Count == 0)
        {
            return line;
        }

        var previous = Resolve(history, index - 1, depth + 1);
        return previous is null ? line : Expand(line, designators, previous);
    }

    private static string LastArgument(string line) => ShellLexer.Words(line).LastOrDefault()?.Text ?? string.Empty;
}
