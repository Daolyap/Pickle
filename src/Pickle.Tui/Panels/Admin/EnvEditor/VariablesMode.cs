using Pickle.Abstractions.Services;

namespace Pickle.Tui.Panels.Admin.EnvEditor;

/// <summary>Variables of one scope. Each change is applied as soon as it is confirmed (a variable is one small write, so there is nothing to stage).</summary>
internal sealed class VariablesMode : EditorMode
{
    private IReadOnlyList<EnvironmentVariable> _variables = [];

    public override string Title => "Variables";

    public override bool UsesScope => true;

    public override string? ReadOnlyReason(IEditorHost host) =>
        host.Scope == EnvironmentScope.Process ? "This process's variables are read-only; pick another scope (F9)." : null;

    public override IReadOnlyList<EditorRow> Rows =>
        [.. _variables.Select((v, i) => new EditorRow(v.Name, Shorten(v.Value), v.Expandable ? "expands" : null, RowTone.Normal, i))];

    public override async Task LoadAsync(IEditorHost host, CancellationToken cancellationToken) =>
        _variables = await host.Variables.ListAsync(host.Scope, cancellationToken).ConfigureAwait(false);

    public override async Task<string?> AddAsync(IEditorHost host, CancellationToken cancellationToken)
    {
        if (host.AskText("Add variable", "NAME=value") is not { } text)
        {
            return Done;
        }

        var eq = text.IndexOf('=', StringComparison.Ordinal);
        return eq <= 0 ? "Expected NAME=value." : await Set(host, text[..eq].Trim(), text[(eq + 1)..], cancellationToken).ConfigureAwait(false);
    }

    public override async Task<string?> EditAsync(IEditorHost host, EditorRow row, CancellationToken cancellationToken)
    {
        var variable = _variables[row.Index];
        return host.AskText("Edit " + variable.Name, "value", variable.Value) is { } value
            ? await Set(host, variable.Name, value, cancellationToken).ConfigureAwait(false)
            : Done;
    }

    public override async Task<string?> DeleteAsync(IEditorHost host, EditorRow row, CancellationToken cancellationToken)
    {
        var variable = _variables[row.Index];
        if (!host.AskYesNo("Remove variable", $"Remove {variable.Name} from the {host.Scope} scope?"))
        {
            return Done;
        }

        return await Set(host, variable.Name, null, cancellationToken).ConfigureAwait(false);
    }

    public override IReadOnlyList<string> Describe(EditorRow row)
    {
        var v = _variables[row.Index];
        return [$"Name:  {v.Name}", $"Scope: {v.Scope}", v.Expandable ? "Kind:  expandable (%NAME% references)" : "Kind:  text", string.Empty, v.Value];
    }

    private async Task<string?> Set(IEditorHost host, string name, string? value, CancellationToken cancellationToken)
    {
        try
        {
            var result = await host.Variables.SetAsync(host.Scope, name, value, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                return result.ShellCommand is { } command ? result.Message + " Run: " + command : result.Message;
            }

            await LoadAsync(host, cancellationToken).ConfigureAwait(false);
            return Done;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }

    private static string Shorten(string value) => value.Length <= 60 ? value : value[..57] + "…";
}
