using Pickle.Abstractions;
using Pickle.Tui;

namespace Pickle.Modules.Example;

/// <summary><c>pk hello [name]</c>: PickleCommandBase gives argument parsing, themed output and error handling.</summary>
internal sealed class HelloCommand(IGreetingService greetings) : PickleCommandBase
{
    public override string Name => "hello";

    public override string Description => "Greet someone (the example module)";

    public override string Usage => "pk hello [name]";

    public override IReadOnlyList<string> Examples => ["pk hello", "pk hello World"];

    protected override Task<int> RunAsync(CommandOutput output, IReadOnlyList<string> raw, CancellationToken cancellationToken)
    {
        var args = CommandArgs.Parse(raw);
        var message = greetings.Greet(args.Rest(0));
        output.Success(message);
        output.Object(Display.Columns(new GreetingRow(message, greetings.CommandsRun), "Message"));
        return Task.FromResult(0);
    }

    private sealed record GreetingRow(string Message, int CommandsRun);
}

/// <summary><c>pk hello-panel</c> (and Alt+Shift+H): a PanelCommand opens the panel interactively and lists otherwise.</summary>
internal sealed class HelloPanelCommand : PanelCommand
{
    public override string Name => "hello-panel";

    public override string Description => "Open the example panel";

    public override string Usage => "pk hello-panel";

    protected override string PanelId => HelloPanel.PanelId;

    protected override Task<int> ListAsync(CommandOutput output, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        foreach (var item in HelloPanel.Items())
        {
            output.Object(item);
        }

        return Task.FromResult(0);
    }
}
