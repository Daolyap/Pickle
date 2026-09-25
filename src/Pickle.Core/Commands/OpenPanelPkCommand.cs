using Pickle.Abstractions;

namespace Pickle.Core.Commands;

/// <summary><c>pk &lt;name&gt; [argument]</c> that opens a panel (plugin panels registered with <c>-Command</c>).</summary>
public sealed class OpenPanelPkCommand(string name, string panelId, string description) : IPickleCommand
{
    public string Name => name;

    public string Description => description;

    public string Usage => $"pk {name} [argument]";

    public ValueTask<int> ExecuteAsync(PickleCommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (context.Pickle.Services.Get<IPanelHost>() is not { } host)
        {
            context.WriteError("Panels are not available in this session.");
            return ValueTask.FromResult(1);
        }

        host.Show(panelId, args.Count == 0 ? null : string.Join(' ', args));
        return ValueTask.FromResult(0);
    }
}
