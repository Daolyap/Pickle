using Pickle.Abstractions.Services;

namespace Pickle.Tui.Panels.Admin.EnvEditor;

internal enum RowTone
{
    Normal,
    Muted,
    Warning,
    Error,
    Good,
}

internal sealed record EditorRow(string Text, string? Detail, string? Hint, RowTone Tone, int Index);

/// <summary>What a mode needs from the panel. Prompts and confirmations block the (background) caller until answered.</summary>
internal interface IEditorHost
{
    IHostsService Hosts { get; }

    IEnvironmentStore Variables { get; }

    EnvironmentScope Scope { get; }

    string CurrentDirectory { get; }

    string? DotEnvPath { get; }

    string? AskText(string title, string label, string initial = "");

    bool AskYesNo(string title, string message);
}

/// <summary>
/// One tab of the editor (hosts, PATH, variables, .env). Edits change a staged copy; <see cref="SaveAsync"/> applies it
/// in one go (one UAC or sudo prompt for the whole change), after the panel showed <see cref="Pending"/>.
/// Action methods return an error message, or null when they worked.
/// </summary>
internal abstract class EditorMode
{
    public abstract string Title { get; }

    public virtual bool UsesScope => false;

    public virtual bool IsDirty => false;

    public abstract IReadOnlyList<EditorRow> Rows { get; }

    /// <summary>Why the current scope can't be changed, or null.</summary>
    public virtual string? ReadOnlyReason(IEditorHost host) => null;

    public abstract Task LoadAsync(IEditorHost host, CancellationToken cancellationToken);

    public virtual Task<string?> AddAsync(IEditorHost host, CancellationToken cancellationToken) => Unsupported();

    public virtual Task<string?> EditAsync(IEditorHost host, EditorRow row, CancellationToken cancellationToken) => Unsupported();

    public virtual Task<string?> DeleteAsync(IEditorHost host, EditorRow row, CancellationToken cancellationToken) => Unsupported();

    /// <summary>Hosts: enable or disable the entry. PATH: move the entry up (<paramref name="delta"/> −1) or down (+1).</summary>
    public virtual Task<string?> MoveOrToggleAsync(IEditorHost host, EditorRow row, int delta, CancellationToken cancellationToken) => Unsupported();

    public virtual Task<ServiceOperationResult> SaveAsync(IEditorHost host, CancellationToken cancellationToken) =>
        Task.FromResult(new ServiceOperationResult(true, "Nothing to save."));

    public abstract IReadOnlyList<string> Describe(EditorRow row);

    /// <summary>The staged change as <c>+</c>/<c>-</c> lines (empty when nothing changed).</summary>
    public virtual IReadOnlyList<string> Pending => [];

    protected static Task<string?> Unsupported() => Task.FromResult<string?>("Not available in this tab.");

    protected static string? Done => null;

    /// <summary>Lines only in <paramref name="after"/> as <c>+ line</c>, only in <paramref name="before"/> as <c>- line</c>.</summary>
    public static IReadOnlyList<string> Diff(IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        var removed = before.ToList();
        var lines = new List<string>();
        foreach (var line in after)
        {
            if (!removed.Remove(line))
            {
                lines.Add("+ " + line);
            }
        }

        lines.AddRange(removed.Select(l => "- " + l));
        return lines;
    }
}
