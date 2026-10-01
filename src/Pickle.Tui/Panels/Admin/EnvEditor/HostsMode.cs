using Pickle.Abstractions.Services;

namespace Pickle.Tui.Panels.Admin.EnvEditor;

internal sealed class HostsMode : EditorMode
{
    private HostsDocument _document = HostsDocument.Parse(string.Empty);
    private string _original = string.Empty;

    public override string Title => "Hosts";

    public override bool IsDirty => _document.Serialize() != _original;

    public override IReadOnlyList<EditorRow> Rows
    {
        get
        {
            var duplicates = _document.Duplicates();
            return [.. _document.Entries.Select((e, i) => new EditorRow(
                $"{e.Address,-16} {string.Join(' ', e.Names)}",
                e.Comment,
                e.Enabled ? (e.Names.Any(duplicates.Contains) ? "duplicate" : null) : "disabled",
                !e.Enabled ? RowTone.Muted : e.Names.Any(duplicates.Contains) ? RowTone.Warning : RowTone.Normal,
                i))];
        }
    }

    public override async Task LoadAsync(IEditorHost host, CancellationToken cancellationToken)
    {
        _document = await host.Hosts.ReadAsync(cancellationToken).ConfigureAwait(false);
        _original = _document.Serialize();
    }

    public override Task<string?> AddAsync(IEditorHost host, CancellationToken cancellationToken)
    {
        var text = host.AskText("Add hosts entry", "address name [name…] [# comment]");
        return Task.FromResult(text is null ? Done : Apply(() => _document.Add(Parse(text))));
    }

    public override Task<string?> EditAsync(IEditorHost host, EditorRow row, CancellationToken cancellationToken)
    {
        var current = _document.Entries[row.Index];
        var text = host.AskText("Edit hosts entry", "address name [name…] [# comment]", HostsDocument.Format(current with { Enabled = true }));
        return Task.FromResult(text is null ? Done : Apply(() => _document.Replace(row.Index, Parse(text) with { Enabled = current.Enabled })));
    }

    public override Task<string?> DeleteAsync(IEditorHost host, EditorRow row, CancellationToken cancellationToken) =>
        Task.FromResult(Apply(() => _document.Remove(row.Index)));

    public override Task<string?> MoveOrToggleAsync(IEditorHost host, EditorRow row, int delta, CancellationToken cancellationToken) =>
        Task.FromResult(Apply(() => _document.SetEnabled(row.Index, !_document.Entries[row.Index].Enabled)));

    public override async Task<ServiceOperationResult> SaveAsync(IEditorHost host, CancellationToken cancellationToken)
    {
        var result = await host.Hosts.WriteAsync(_document, cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            _original = _document.Serialize();
        }

        return result;
    }

    public override IReadOnlyList<string> Describe(EditorRow row)
    {
        var entry = _document.Entries[row.Index];
        return
        [
            $"Address: {entry.Address}",
            $"Names:   {string.Join(", ", entry.Names)}",
            $"State:   {(entry.Enabled ? "active" : "disabled (commented out)")}",
            .. entry.Comment is { } c ? [$"Comment: {c}"] : Array.Empty<string>(),
            string.Empty,
        ];
    }

    public override IReadOnlyList<string> Pending =>
        Diff(_original.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n'), _document.Serialize().Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n'));

    private static HostsEntry Parse(string text) =>
        HostsDocument.TryParseLine(text) is { } entry
            ? entry with { Enabled = true }
            : throw new ArgumentException("Expected: 10.0.0.5 web web.lan  # optional comment");

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
