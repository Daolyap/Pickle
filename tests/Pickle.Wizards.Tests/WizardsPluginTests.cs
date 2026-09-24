using Pickle.Abstractions;
using Pickle.Testing;
using Pickle.Testing.Fakes;

namespace Pickle.Wizards.Tests;

public class WizardsPluginTests
{
    [Fact]
    public void HasStableId() => Assert.Equal("pickle.wizards", new WizardsPlugin().Id);

    [Fact]
    public void RegistersBuiltInWizardsAndCommand()
    {
        using var t = TestPickle.Create();
        new WizardsPlugin().Initialize(t.Runtime);

        Assert.Equal(WizardLoader.EmbeddedNames.Count, t.Runtime.Wizards.All.Count);
        Assert.Equal("curl", t.Runtime.Wizards.FindForCommand("curl.exe")?.Id);
        Assert.Equal("get-winevent", t.Runtime.Wizards.FindForCommand("get-winevent")?.Id);
        Assert.NotNull(t.Runtime.Commands.Get("wizard"));
    }

    [Fact]
    public void UserFilesOverrideBuiltInsAndBrokenFilesAreSkipped()
    {
        using var t = TestPickle.Create();
        File.WriteAllText(Path.Combine(t.Paths.WizardsDir, "curl.json"), """
            { "id": "curl", "title": "My curl", "command": "curl", "sections": [] }
            """);
        File.WriteAllText(Path.Combine(t.Paths.WizardsDir, "hello.json"), """
            { "id": "hello", "title": "Hello", "command": "hello",
              "sections": [{ "title": "x", "options": [{ "id": "name", "label": "Name", "type": "positional", "position": 1 }] }] }
            """);
        File.WriteAllText(Path.Combine(t.Paths.WizardsDir, "broken.json"), "{ not json");

        new WizardsPlugin().Initialize(t.Runtime);

        Assert.Equal("My curl", t.Runtime.Wizards.Get("curl")?.Title);
        Assert.NotNull(t.Runtime.Wizards.Get("hello"));
    }

    [Fact]
    public async Task ListPrintAndPresets()
    {
        using var t = TestPickle.Create();
        new WizardsPlugin().Initialize(t.Runtime);
        var command = t.Runtime.Commands.Get("wizard")!;

        var (list, _) = await Run(t, command, "list");
        Assert.Contains(list.OfType<WizardInfo>(), w => w.Id == "git" && w.Modes.Contains("commit", StringComparison.Ordinal));

        var (printed, _) = await Run(t, command, "curl", "--print", "--preset", "POST JSON");
        Assert.Equal(["curl --json '{\"name\":\"pickle\",\"crunchy\":true}' https://httpbin.org/post"], printed);

        var (empty, _) = await Run(t, command, "git", "--print", "--mode", "log");
        Assert.Equal(["git log"], empty);

        var (presets, _) = await Run(t, command, "tar", "--presets");
        Assert.All(presets.OfType<WizardPresetInfo>(), p => Assert.StartsWith("tar ", p.CommandLine, StringComparison.Ordinal));

        var (_, errors) = await Run(t, command, "nope");
        Assert.NotEmpty(errors);
    }

    [Fact]
    public async Task OpensThePanelAndAppliesItsResult()
    {
        using var t = TestPickle.Create();
        new WizardsPlugin().Initialize(t.Runtime);
        var host = new FakePanelHost { NextResult = new PanelResult(PanelResultKind.ReplaceInput, "curl https://x.test") };
        t.Runtime.Services.Add<IPanelHost>(host);

        var (_, errors) = await Run(t, t.Runtime.Commands.Get("wizard")!, interactive: true, "curl");

        Assert.Empty(errors);
        Assert.Equal([("wizard", (string?)"curl", (string?)null)], host.Shown);
    }

    private static Task<(List<object?> Output, List<string> Errors)> Run(TestPickle t, IPickleCommand command, params string[] args) =>
        Run(t, command, interactive: false, args);

    private static async Task<(List<object?> Output, List<string> Errors)> Run(TestPickle t, IPickleCommand command, bool interactive, params string[] args)
    {
        var output = new List<object?>();
        var errors = new List<string>();
        var context = new PickleCommandContext
        {
            Pickle = t.Runtime,
            WriteObject = output.Add,
            WriteHost = _ => { },
            WriteError = errors.Add,
            Confirm = (_, d) => d,
            Interactive = interactive,
        };
        await command.ExecuteAsync(context, args, CancellationToken.None);
        return (output, errors);
    }
}
