using Pickle.Abstractions;

namespace Pickle.Tui;

/// <summary>
/// A <c>pk</c> command that opens a panel in an interactive session and prints a table (objects for scripts) otherwise:
/// <c>pk services</c>, <c>pk docker</c>, … Derive and implement <see cref="ListAsync"/>.
/// </summary>
public abstract class PanelCommand : PickleCommandBase
{
    protected abstract string PanelId { get; }

    /// <summary>The non-interactive output: write rows with <see cref="CommandOutput.Object"/>.</summary>
    protected abstract Task<int> ListAsync(CommandOutput output, IReadOnlyList<string> args, CancellationToken cancellationToken);

    protected override Task<int> RunAsync(CommandOutput output, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var pickle = output.Pickle;
        var listing = args.Count > 0 && args[0] is "list" or "ls" or "--list";
        if (listing || !pickle.Shell.IsInteractive || pickle.Services.Get<IPanelHost>() is not { } host)
        {
            return ListAsync(output, listing ? [.. args.Skip(1)] : args, cancellationToken);
        }

        var result = host.Show(PanelId, args.Count == 0 ? null : string.Join(' ', args));
        switch (result?.Kind)
        {
            case PanelResultKind.RunCommand:
                pickle.Shell.SubmitCommand(result.Text);
                break;
            case PanelResultKind.ReplaceInput:
                pickle.Shell.ReplaceInput(result.Text);
                break;
            case PanelResultKind.InsertText:
                pickle.Shell.InsertText(result.Text);
                break;
            case PanelResultKind.ChangeDirectory:
                pickle.Shell.SetLocation(result.Text);
                break;
        }

        return Task.FromResult(0);
    }
}
