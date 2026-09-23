using Pickle.Core.Contracts;

namespace Pickle.Core.Syntax;

/// <summary>FOUNDATION PLACEHOLDER (workstream W1 replaces this file): no highlighting.</summary>
public sealed class SyntaxHighlighter : ISyntaxHighlighter
{
    private readonly PickleRuntime _runtime;

    public SyntaxHighlighter(PickleRuntime runtime) => _runtime = runtime;

    public IReadOnlyList<StyledSpan> Highlight(string input) => [];
}
