using Pickle.Abstractions;
using Pickle.Tui;
using Terminal.Gui.Input;

namespace Pickle.Modules.Example;

internal sealed record HelloItem(string Name, string Greeting);

/// <summary>A <see cref="ResourcePanel{T}"/> is a list, a details pane and actions; most module panels are one.</summary>
internal sealed class HelloPanel : ResourcePanel<HelloItem>
{
    public const string PanelId = "hello";

    public static PanelDescriptor Descriptor { get; } = new()
    {
        Id = PanelId,
        Title = "Hello",
        Description = "The example module's panel",
        DefaultKey = "Alt+Shift+H",
        CreateView = context => new HelloPanel(context),
    };

    public HelloPanel(PanelContext context)
        : base(context, "Hello", i => i.Name)
    {
        // F2 closes the panel and runs a command in the shell; AddAction would run code in the background instead.
        AddCommand(Key.F2, "Say it", i => PowerShellQuote.Command("pk", "hello", i.Name));
    }

    public static IReadOnlyList<HelloItem> Items() =>
        [.. new[] { "World", "Pickle", "Reader" }.Select(n => new HelloItem(n, "Hello, " + n + "!"))];

    protected override Task<IReadOnlyList<HelloItem>> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Items());

    protected override Task<IReadOnlyList<string>> DescribeAsync(HelloItem item, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([item.Greeting, string.Empty, "Run it: pk hello " + item.Name]);
}
