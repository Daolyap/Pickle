using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Modules;
using Pickle.Modules.Example;
using Pickle.Testing;
using Pickle.Tui.Tests;
using Terminal.Gui.Input;

namespace Pickle.Modules.Tests.Example;

public class ExampleModuleTests
{
    private static TestPickle Start(Action<PickleConfig>? configure = null) =>
        TestPickle.Create(start: true, configure: configure, plugins: [new Pickle.Tui.TuiPlugin()], modules: OptionalModules.All, machineModules: ["example"]);

    [Fact]
    public void TheModuleIsHiddenButLoadsWhenSelected()
    {
        using var t = Start();

        Assert.True(OptionalModules.All.Single(m => m.Id == "example").Hidden);
        Assert.Equal(ModuleState.Loaded, t.Runtime.ModuleCatalog.Find("example")!.State);
        Assert.NotNull(t.Runtime.CommandRegistry.Get("hello"));
        Assert.NotNull(t.Runtime.PanelRegistry.Get("hello"));
        Assert.NotNull(t.Runtime.PromptSegmentRegistry.Get("hello"));
    }

    [Fact]
    public void HelloUsesTheSettingFromConfig()
    {
        using var t = Start(c => c.Extensions["example"] = System.Text.Json.JsonSerializer.SerializeToElement(new { greeting = "Hola" }));

        var lines = t.Run("pk hello World | ForEach-Object Message");

        Assert.Equal("Hola, World!", lines[0]);
    }

    [Fact]
    public async Task TheHookCountsCommandsAndTheSegmentShowsThem()
    {
        using var t = Start();
        var greetings = t.Runtime.Services.Require<IGreetingService>();
        await t.Runtime.HookRegistry.RaiseAsync(new HookEvent(HookKind.PostExecute, "ls"));
        await t.Runtime.HookRegistry.RaiseAsync(new HookEvent(HookKind.PostExecute, "ls"));

        var segment = t.Runtime.PromptSegmentRegistry.Get("hello")!;
        var output = await segment.RenderAsync(t.Runtime.CreatePromptContext(), new SegmentStyle { Type = "hello" }, CancellationToken.None);

        Assert.Equal(2, greetings.CommandsRun);
        Assert.Equal("👋 2", output!.Text);
    }

    [Fact]
    public async Task TheSegmentHidesWhenSwitchedOff()
    {
        using var t = Start(c => c.Extensions["example"] = System.Text.Json.JsonSerializer.SerializeToElement(new { showSegment = false }));
        var segment = t.Runtime.PromptSegmentRegistry.Get("hello")!;

        var output = await segment.RenderAsync(t.Runtime.CreatePromptContext(), new SegmentStyle { Type = "hello" }, CancellationToken.None);

        Assert.Null(output);
    }

    [Fact]
    public void ThePanelListsItemsAndRunsACommand()
    {
        var script = new UiScript()
            .WaitFor("loaded", app => TuiHarness.Top<HelloPanel>(app).List.TotalCount == 3)
            .Press(Key.F2);
        var (t, host) = TuiHarness.Start(script, modules: OptionalModules.All, machineModules: ["example"]);
        using var _ = t;

        var result = host.Show("hello");

        script.AssertOk();
        Assert.Equal(new PanelResult(PanelResultKind.RunCommand, "pk hello World"), result);
    }
}
