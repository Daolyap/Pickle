using System.Text;
using Pickle.Abstractions.Services;

namespace Pickle.Tui.Panels.Admin.EnvEditor;

/// <summary>A <c>.env</c> file in the current folder (or the one the panel was opened with): staged, then written in place.</summary>
internal sealed class DotEnvMode : EditorMode
{
    private DotEnvDocument _document = DotEnvDocument.Parse(string.Empty);
    private string _original = string.Empty;
    private string _path = string.Empty;

    public override string Title => ".env";

    public override bool IsDirty => _document.Serialize() != _original;

    public override IReadOnlyList<EditorRow> Rows =>
        [.. _document.Variables.Select((v, i) => new EditorRow(v.Name, Shorten(v.Value), null, RowTone.Normal, i))];

    public override Task LoadAsync(IEditorHost host, CancellationToken cancellationToken)
    {
        _path = host.DotEnvPath ?? Path.Combine(host.CurrentDirectory, ".env");
        _original = File.Exists(_path) ? File.ReadAllText(_path) : string.Empty;
        _document = DotEnvDocument.Parse(_original);
        return Task.CompletedTask;
    }

    public override Task<string?> AddAsync(IEditorHost host, CancellationToken cancellationToken)
    {
        if (host.AskText("Add to " + Path.GetFileName(_path), "NAME=value") is not { } text)
        {
            return Task.FromResult(Done);
        }

        var eq = text.IndexOf('=', StringComparison.Ordinal);
        return Task.FromResult(eq <= 0 ? "Expected NAME=value." : Apply(() => _document.Set(text[..eq].Trim(), text[(eq + 1)..])));
    }

    public override Task<string?> EditAsync(IEditorHost host, EditorRow row, CancellationToken cancellationToken)
    {
        var variable = _document.Variables[row.Index];
        return Task.FromResult(host.AskText("Edit " + variable.Name, "value", variable.Value) is { } value ? Apply(() => _document.Set(variable.Name, value)) : Done);
    }

    public override Task<string?> DeleteAsync(IEditorHost host, EditorRow row, CancellationToken cancellationToken) =>
        Task.FromResult(Apply(() => _document.Remove(_document.Variables[row.Index].Name)));

    public override Task<ServiceOperationResult> SaveAsync(IEditorHost host, CancellationToken cancellationToken)
    {
        try
        {
            var text = _document.Serialize();
            var temp = _path + ".pickle-new";
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            File.Move(temp, _path, overwrite: true);
            _original = text;
            return Task.FromResult(new ServiceOperationResult(true, "Saved " + _path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(new ServiceOperationResult(false, ex.Message));
        }
    }

    public override IReadOnlyList<string> Describe(EditorRow row)
    {
        var variable = _document.Variables[row.Index];
        return [$"File:  {_path}", $"Name:  {variable.Name}", string.Empty, variable.Value];
    }

    public override IReadOnlyList<string> Pending =>
        Diff(_original.TrimEnd('\n').Split('\n'), _document.Serialize().TrimEnd('\n').Split('\n'));

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

    private static string Shorten(string value) => value.Length <= 60 ? value : value[..57] + "…";
}
