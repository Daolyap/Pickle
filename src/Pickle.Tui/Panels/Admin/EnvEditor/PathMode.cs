using Pickle.Abstractions.Services;

namespace Pickle.Tui.Panels.Admin.EnvEditor;

internal sealed class PathMode : EditorMode
{
    private PathList _list = new([]);
    private IReadOnlyList<string> _original = [];
    private EnvironmentScope _scope;

    public override string Title => "PATH";

    public override bool UsesScope => true;

    public override bool IsDirty => !_list.Items.SequenceEqual(_original);

    public override string? ReadOnlyReason(IEditorHost host) =>
        host.Scope == EnvironmentScope.Process ? "The live PATH of this process is read-only; pick another scope (F9)." : null;

    public override IReadOnlyList<EditorRow> Rows =>
    [
        .. _list.Items.Select((entry, i) =>
        {
            var problem = _list.Problem(i, Directory.Exists, Environment.ExpandEnvironmentVariables);
            return new EditorRow(entry, null, problem, problem switch { null => RowTone.Normal, "duplicate" => RowTone.Warning, _ => RowTone.Error }, i);
        }),
    ];

    public override async Task LoadAsync(IEditorHost host, CancellationToken cancellationToken)
    {
        _scope = host.Scope;
        _original = await host.Variables.GetPathAsync(host.Scope, cancellationToken).ConfigureAwait(false);
        _list = new PathList(_original);
    }

    public override Task<string?> AddAsync(IEditorHost host, CancellationToken cancellationToken) =>
        Task.FromResult(host.AskText("Add to PATH", "folder", host.CurrentDirectory) is { } folder ? Apply(() => _list.Add(folder)) : Done);

    public override Task<string?> EditAsync(IEditorHost host, EditorRow row, CancellationToken cancellationToken)
    {
        if (host.AskText("Edit PATH entry", "folder", _list.Items[row.Index]) is not { } folder)
        {
            return Task.FromResult(Done);
        }

        return Task.FromResult(Apply(() =>
        {
            _list.Add(folder, row.Index);
            _list.RemoveAt(row.Index + 1);
        }));
    }

    public override Task<string?> DeleteAsync(IEditorHost host, EditorRow row, CancellationToken cancellationToken) =>
        Task.FromResult(Apply(() => _list.RemoveAt(row.Index)));

    public override Task<string?> MoveOrToggleAsync(IEditorHost host, EditorRow row, int delta, CancellationToken cancellationToken) =>
        Task.FromResult(_list.Move(row.Index, delta) ? Done : "It is already at the end of the list.");

    public override async Task<ServiceOperationResult> SaveAsync(IEditorHost host, CancellationToken cancellationToken)
    {
        var result = await host.Variables.SetPathAsync(_scope, _list.Items, cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            _original = [.. _list.Items];
        }

        return result;
    }

    public override IReadOnlyList<string> Describe(EditorRow row)
    {
        var entry = _list.Items[row.Index];
        var expanded = Environment.ExpandEnvironmentVariables(entry);
        return
        [
            $"Entry:    {entry}",
            .. expanded != entry ? [$"Expands:  {expanded}"] : Array.Empty<string>(),
            $"Position: {row.Index + 1} of {_list.Items.Count} (earlier entries win)",
            $"Status:   {row.Hint ?? "ok"}",
        ];
    }

    public override IReadOnlyList<string> Pending => Diff(_original, _list.Items);

    private static string? Apply(Action change)
    {
        try
        {
            change();
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }
}
