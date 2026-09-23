using System.Management.Automation.Runspaces;
using Pickle.Abstractions;

namespace Pickle.Core.Contracts;

// Internal component seams between Core subsystems. Each is implemented by one workstream and consumed by
// others through PickleRuntime. Keep these small and stable.

/// <summary>A rendered prompt. <see cref="Left"/> may contain newlines; input starts after its last line.</summary>
public sealed record PromptRender(string Left, string? Right, string Continuation);

public interface IPromptRenderer
{
    PromptRender Render(PromptContext context);

    /// <summary>Short prompt that replaces the full prompt in scrollback once a line is accepted (transient prompt).</summary>
    string RenderTransient(PromptContext context);

    /// <summary>Kick off async segment refresh (git etc.) ahead of the next Render.</summary>
    void Prefetch(PromptContext context);
}

public interface ILineEditor
{
    /// <summary>Read one logical (possibly multi-line) command. Returns null on EOF (Ctrl+D on an empty line).</summary>
    string? ReadLine(PromptRender prompt, PromptContext promptContext, CancellationToken cancellationToken = default);

    /// <summary>Minimal line input for Read-Host / host prompts. <paramref name="mask"/> hides typed characters.</summary>
    string? ReadSimpleLine(string prompt, bool mask);
}

public interface ISyntaxHighlighter
{
    /// <summary>Per-character SGR style runs for <paramref name="input"/> (styles are full SGR sequences).</summary>
    IReadOnlyList<StyledSpan> Highlight(string input);
}

public readonly record struct StyledSpan(int Start, int Length, string Style);

public interface IAutosuggestProvider
{
    /// <summary>Full-line suggestion that starts with <paramref name="input"/>, or null.</summary>
    string? Suggest(string input, string cwd);
}

public interface ICompletionEngine
{
    /// <summary>PowerShell CommandCompletion merged with registered providers and ranked.</summary>
    Task<CompletionSet> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default);
}

public sealed record TranslationOutcome(string Command, string? Explanation)
{
    public bool Changed { get; init; }
}

public interface ITranslationPipeline
{
    /// <summary>Apply input rewriters to an accepted line.</summary>
    TranslationOutcome Translate(string input, string cwd);

    /// <summary>PowerShell source for all shim functions (imported into the runspace at startup).</summary>
    string BuildShimScript();
}

public interface IAliasMaterializer
{
    /// <summary>PowerShell source that defines all active aliases as functions.</summary>
    string BuildAliasScript(string cwd);
}

public interface IPluginManager
{
    /// <summary>Discover and initialize plugins (built-ins are passed in by the composition root).</summary>
    void LoadAll(IReadOnlyList<IPicklePlugin> builtIns);

    IReadOnlyList<LoadedPlugin> Loaded { get; }
}

public sealed record LoadedPlugin(string Id, string DisplayName, string Kind, string? Path, bool Enabled, string? Error);

public interface ISyncService
{
    Task<SyncReport> PushAsync(CancellationToken cancellationToken = default);

    Task<SyncReport> PullAsync(CancellationToken cancellationToken = default);

    Task<SyncReport> StatusAsync(CancellationToken cancellationToken = default);

    Task<SyncReport> InitAsync(string backend, string target, CancellationToken cancellationToken = default);
}

public sealed record SyncReport(bool Success, string Message, IReadOnlyList<string> Changes);

/// <summary>Extra InitialSessionState contributions (cmdlets, modules, variables) collected at startup.</summary>
public interface ISessionStateContributor
{
    void Contribute(InitialSessionState state);
}

/// <summary>Lifecycle hooks for components constructed by PickleRuntime.</summary>
public interface IRuntimeComponent
{
    /// <summary>Register key actions, `pk` commands, prompt segments, session contributors. Runspace not open yet.</summary>
    void Initialize()
    {
    }

    /// <summary>Runspace is open and plugins are loaded (define functions, import shims, warm caches).</summary>
    void OnStarted()
    {
    }
}
