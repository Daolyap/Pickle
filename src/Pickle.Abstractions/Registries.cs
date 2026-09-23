namespace Pickle.Abstractions;

// ───────────────────────────── Aliases ─────────────────────────────

public enum AliasKind
{
    /// <summary><c>ll</c> → <c>Get-ChildItem -Force</c>; extra arguments are appended.</summary>
    Simple,

    /// <summary><c>gco {branch}</c> → <c>git checkout {branch}</c>; supports <c>{name=default}</c> and <c>{*}</c> (rest).</summary>
    Parameterized,

    /// <summary>Body is a full PowerShell function body (may declare its own param block).</summary>
    Script,
}

public sealed class AliasDefinition
{
    public string Name { get; set; } = string.Empty;
    public AliasKind Kind { get; set; } = AliasKind.Simple;
    public string Body { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Only active when the current directory matches this glob (e.g. <c>~/src/**</c>).</summary>
    public string? DirectoryScope { get; set; }

    /// <summary>Only active on this machine name.</summary>
    public string? MachineScope { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public interface IAliasRegistry
{
    IReadOnlyList<AliasDefinition> All { get; }

    event EventHandler? Changed;

    AliasDefinition? Get(string name);

    /// <summary>Adds or replaces, persists, and (re)materializes the PowerShell function.</summary>
    void Set(AliasDefinition alias);

    bool Remove(string name);
}

// ───────────────────────────── History ─────────────────────────────

public sealed record HistoryEntry(
    string CommandLine,
    DateTimeOffset Timestamp,
    string? Cwd = null,
    bool? Success = null,
    long? DurationMs = null,
    string? SessionId = null,
    string? Machine = null);

public interface IHistoryStore
{
    /// <summary>Oldest first.</summary>
    IReadOnlyList<HistoryEntry> Entries { get; }

    string SessionId { get; }

    /// <summary>Adds (subject to ignore/secret filters). Returns false if filtered out.</summary>
    bool Add(HistoryEntry entry);

    /// <summary>Update the last entry's outcome after it finished running.</summary>
    void CompleteLast(bool success, long durationMs);

    void Clear();
}

// ───────────────────────────── Prompt segments ─────────────────────────────

public sealed record PromptContext(
    string Cwd,
    bool LastCommandSucceeded,
    int? LastExitCode,
    TimeSpan? LastCommandDuration,
    bool IsAdmin,
    int JobCount,
    string UserName,
    string HostName,
    DateTimeOffset Now,
    int TerminalWidth,
    IPickleContext? Pickle = null);

/// <summary>Rendered segment text; colors override the theme's segment style when non-null.</summary>
public sealed record PromptSegmentOutput(string Text, string? Foreground = null, string? Background = null);

public interface IPromptSegment
{
    /// <summary>Matches <see cref="SegmentStyle.Type"/>.</summary>
    string Type { get; }

    /// <summary>Return null to hide the segment.</summary>
    ValueTask<PromptSegmentOutput?> RenderAsync(PromptContext context, SegmentStyle style, CancellationToken cancellationToken);
}

public interface IPromptSegmentRegistry
{
    void Register(IPromptSegment segment);

    IPromptSegment? Get(string type);

    IReadOnlyCollection<IPromptSegment> All { get; }
}

// ───────────────────────────── Completion ─────────────────────────────

public enum CompletionKind
{
    Command,
    Parameter,
    ParameterValue,
    File,
    Directory,
    Variable,
    Property,
    Method,
    Type,
    Keyword,
    Alias,
    History,
    Wizard,
    Text,
    Other,
}

public sealed record CompletionItem(
    string CompletionText,
    string ListText,
    CompletionKind Kind,
    string? Description = null);

public sealed record CompletionRequest(string Input, int Cursor, string Cwd);

public sealed record CompletionSet(int ReplacementIndex, int ReplacementLength, IReadOnlyList<CompletionItem> Items)
{
    public static CompletionSet Empty(int cursor) => new(cursor, 0, []);
}

public interface ICompletionProvider
{
    string Name { get; }

    /// <summary>Higher runs first; results from all providers are merged then de-duplicated by CompletionText.</summary>
    int Priority { get; }

    /// <summary>Return null when not applicable to this input.</summary>
    ValueTask<CompletionSet?> GetCompletionsAsync(CompletionRequest request, CancellationToken cancellationToken);
}

public interface ICompletionRegistry
{
    void Register(ICompletionProvider provider);

    IReadOnlyList<ICompletionProvider> Providers { get; }
}

// ───────────────────────────── Key bindings ─────────────────────────────

/// <summary>What an editor action can do to the input line.</summary>
public interface IEditorBuffer
{
    string Text { get; }

    int Cursor { get; }

    void Insert(string text);

    void Replace(string text, int cursor);

    /// <summary>Accept the line (as if Enter was pressed).</summary>
    void Accept();

    /// <summary>Force a full redraw (e.g. after a panel wrote to the screen).</summary>
    void Redraw();

    /// <summary>Open an inline overlay (completion menu, history search). Replaces any open overlay.</summary>
    void OpenOverlay(IEditorOverlay overlay);

    void CloseOverlay();

    /// <summary>Run a full-screen panel from inside the editor, applying its result to the buffer/shell.</summary>
    void ShowPanel(string panelId, string? argument = null);

    /// <summary>The current autosuggestion (full line) or null.</summary>
    string? Suggestion { get; }
}

public delegate ValueTask EditorAction(IEditorBuffer buffer, CancellationToken cancellationToken);

public sealed record EditorActionInfo(string Name, string Description, EditorAction Handler);

public interface IKeyBindingRegistry
{
    /// <summary>Register a named action (e.g. "panel.git"). Built-in editor actions are registered by the line editor.</summary>
    void RegisterAction(string name, string description, EditorAction handler);

    /// <summary>Bind a chord like "Ctrl+R", "Alt+G", "F1", "Shift+Enter", "Ctrl+Spacebar" to an action name.</summary>
    void Bind(string chord, string actionName);

    void Unbind(string chord);

    EditorActionInfo? GetAction(string name);

    IReadOnlyDictionary<string, string> Bindings { get; }

    IReadOnlyCollection<EditorActionInfo> Actions { get; }
}

// ───────────────────────────── `pk` commands ─────────────────────────────

public sealed class PickleCommandContext
{
    public required IPickleContext Pickle { get; init; }

    /// <summary>Emit an object to the PowerShell pipeline.</summary>
    public required Action<object?> WriteObject { get; init; }

    /// <summary>Write a line to the host (ANSI allowed).</summary>
    public required Action<string> WriteHost { get; init; }

    public required Action<string> WriteError { get; init; }

    /// <summary>Ask a yes/no question on the terminal. Returns the default when not interactive.</summary>
    public required Func<string, bool, bool> Confirm { get; init; }

    public bool Interactive { get; init; }

    public string Cwd { get; init; } = Environment.CurrentDirectory;
}

/// <summary>A <c>pk &lt;name&gt; ...</c> subcommand (also reachable as <c>pickle &lt;name&gt;</c>).</summary>
public interface IPickleCommand
{
    string Name { get; }

    string Description { get; }

    /// <summary>One-line usage, e.g. "pk update check|install [--all] [--kb KB123]".</summary>
    string Usage { get; }

    /// <summary>Return a process-style exit code (0 = success).</summary>
    ValueTask<int> ExecuteAsync(PickleCommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken);
}

public interface ICommandRegistry
{
    void Register(IPickleCommand command);

    IPickleCommand? Get(string name);

    IReadOnlyCollection<IPickleCommand> All { get; }
}

// ───────────────────────────── Translation ─────────────────────────────

public sealed record RewriteContext(string Cwd, IReadOnlyList<string> RecentHistory);

public sealed record RewriteResult(string Rewritten, string? Explanation = null);

/// <summary>Source-level rewrite applied to an accepted line before execution (e.g. <c>VAR=1 cmd</c>, <c>2>/dev/null</c>, <c>!!</c>).</summary>
public interface IInputRewriter
{
    string Name { get; }

    int Order { get; }

    /// <summary>Return null if the input is untouched.</summary>
    RewriteResult? Rewrite(string input, RewriteContext context);
}

public sealed record TranslationShim(string Name, string Description, string FunctionBody);

public interface ITranslationRegistry
{
    void RegisterRewriter(IInputRewriter rewriter);

    /// <summary>Register a PowerShell function shim (e.g. a POSIX-style command implemented in PowerShell).</summary>
    void RegisterShim(TranslationShim shim);

    IReadOnlyList<IInputRewriter> Rewriters { get; }

    IReadOnlyList<TranslationShim> Shims { get; }
}

// ───────────────────────────── Hooks ─────────────────────────────

public enum HookKind
{
    PreExecute,
    PostExecute,
    Prompt,
    DirectoryChanged,
    Exit,
}

public sealed record HookEvent(
    HookKind Kind,
    string? CommandLine = null,
    string? Cwd = null,
    string? PreviousCwd = null,
    bool? Success = null,
    TimeSpan? Duration = null);

public interface IHookRegistry
{
    IDisposable Register(HookKind kind, Func<HookEvent, CancellationToken, ValueTask> handler);

    ValueTask RaiseAsync(HookEvent hookEvent, CancellationToken cancellationToken = default);
}
