using Pickle.Abstractions;
using Pickle.Core.Contracts;

namespace Pickle.Core.Completion;

/// <summary>
/// FOUNDATION PLACEHOLDER (workstream W2 replaces this file): returns nothing. W2 wraps
/// System.Management.Automation.CommandCompletion.CompleteInput, merges registered providers, ranks with the
/// fuzzy matcher, and registers the Tab / Shift+Tab actions that open the completion menu overlay.
/// </summary>
public sealed class CompletionEngine : ICompletionEngine
{
    private readonly PickleRuntime _runtime;

    public CompletionEngine(PickleRuntime runtime) => _runtime = runtime;

    public Task<CompletionSet> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(CompletionSet.Empty(request.Cursor));
}
