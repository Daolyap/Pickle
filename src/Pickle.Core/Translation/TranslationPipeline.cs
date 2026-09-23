using Pickle.Abstractions;
using Pickle.Core.Contracts;

namespace Pickle.Core.Translation;

/// <summary>
/// FOUNDATION PLACEHOLDER (workstream W4 completes this file): runs registered <see cref="IInputRewriter"/>s.
/// W4 adds the built-in rewriters (VAR=x cmd, export, /dev/null, !!, !$, sudo, apt/brew install), the
/// Pickle.Translate shim module, and the command-not-found handler.
/// </summary>
public sealed class TranslationPipeline : ITranslationPipeline
{
    private readonly PickleRuntime _runtime;

    public TranslationPipeline(PickleRuntime runtime) => _runtime = runtime;

    public TranslationOutcome Translate(string input, string cwd)
    {
        if (!_runtime.Config.Current.Translation.Enabled)
        {
            return new TranslationOutcome(input, null);
        }

        var current = input;
        string? explanation = null;
        var context = new RewriteContext(cwd, [.. _runtime.History.Entries.TakeLast(20).Select(e => e.CommandLine)]);
        foreach (var rewriter in _runtime.TranslationRegistry.Rewriters)
        {
            if (rewriter.Rewrite(current, context) is { } result && result.Rewritten != current)
            {
                current = result.Rewritten;
                explanation = result.Explanation ?? explanation;
            }
        }

        return new TranslationOutcome(current, explanation) { Changed = current != input };
    }

    public string BuildShimScript() => string.Empty;
}
