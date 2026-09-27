using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Testing;
using Pickle.Testing.Fakes;
using Pickle.Tui.Panels.Devious;
using Pickle.Tui.Tests.SystemMonitoring;
using static Pickle.Tui.Tests.Windows.PanelRunner;

namespace Pickle.Tui.Tests.Devious;

public sealed class DeviousTests : IDisposable
{
    private readonly TestPickle _t = TestPickle.Create();
    private readonly string _project = Directory.CreateTempSubdirectory("pickle-devious").FullName;

    public DeviousTests()
    {
        var services = _t.Runtime.ServiceRegistry;
        var processes = new FakeProcessMonitor();
        processes.Processes.Add(new ProcessSample(4242, "hyperdrive", 64.0, 900L << 20, 12, null));
        processes.Processes.Add(new ProcessSample(7, "idle-thing", 0.1, 10L << 20, 1, null));
        services.Add<IProcessMonitor>(processes);
        var network = new FakeNetworkMonitor();
        network.Connections.Add(new NetworkConnection("TCP", "10.0.0.5", 51000, "140.82.112.3", 443, "ESTABLISHED", 4242, "hyperdrive"));
        services.Add<INetworkMonitor>(network);
        services.Add<ISystemInfo>(new FakeSystemInfo());

        Directory.CreateDirectory(Path.Combine(_project, "src"));
        File.WriteAllText(Path.Combine(_project, "src", "engine.cs"), "public static class Engine\n{\n    // warp\n    public static int Speed = 9;\n}\n");
        File.WriteAllText(Path.Combine(_project, "README.md"), "# Devious\n");
        Directory.CreateDirectory(Path.Combine(_project, "node_modules", "left-pad"));
        File.WriteAllText(Path.Combine(_project, "node_modules", "left-pad", "index.js"), "module.exports = 1;\n");
    }

    public void Dispose()
    {
        _t.Dispose();
        try
        {
            Directory.Delete(_project, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<DeviousSources> Loaded()
    {
        var sources = new DeviousSources(_t.Runtime, _project, seed: 7);
        await sources.LoadOnceAsync(CancellationToken.None);
        return sources;
    }

    [Fact]
    public async Task SourcesGatherRealDataFromTheFolderAndMonitors()
    {
        var sources = await Loaded();

        Assert.Equal(_project, sources.Tree[0]);
        Assert.Contains("├── node_modules/", sources.Tree);
        Assert.DoesNotContain(sources.Tree, l => l.Contains("left-pad", StringComparison.Ordinal));
        Assert.Contains(sources.Tree, l => l.EndsWith("engine.cs", StringComparison.Ordinal));
        Assert.True(sources.HasCode);
        Assert.Equal(Path.Combine("src", "engine.cs"), sources.NextCode()?.Name);
        Assert.True(sources.TryTakeHash(out var hash));
        Assert.Matches("^[0-9a-f]{64}  ", hash);
        Assert.Equal("hyperdrive", sources.Connections.Single().ProcessName);
    }

    [Fact]
    public async Task ScreenFillsTheTerminalWithDistinctPanes()
    {
        var sources = await Loaded();
        var screen = new DeviousScreen(sources, seed: 3);
        screen.Render(160, 48);
        for (var i = 0; i < 40; i++)
        {
            screen.Advance(TimeSpan.FromMilliseconds(100));
        }

        var frame = screen.Render(160, 48);
        var text = string.Join('\n', frame.PlainLines());
        if (Environment.GetEnvironmentVariable("PICKLE_DUMP_SCREENS") is { Length: > 0 } dir)
        {
            File.WriteAllText(Path.Combine(dir, "devious.txt"), text);
        }

        Assert.InRange(screen.Layout.Count, 4, 8);
        Assert.Equal(screen.Layout.Count, screen.Layout.Select(l => l.Pane.GetType()).Distinct().Count());
        Assert.Equal(160 * 48, screen.Layout.Sum(l => l.Box.Width * l.Box.Height));
        Assert.All(screen.Layout, l => Assert.Contains(l.Pane.Title.Split(' ')[0], text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task EveryPaneDrawsWithinItsBox()
    {
        var sources = await Loaded();
        var screen = new DeviousScreen(sources, seed: 11);
        for (var seed = 0; seed < 6; seed++)
        {
            screen.Shuffle();
            screen.Render(90, 30);
            for (var i = 0; i < 30; i++)
            {
                screen.Advance(TimeSpan.FromMilliseconds(120));
            }

            var lines = screen.Render(90, 30).PlainLines();
            Assert.Equal(30, lines.Count);
            Assert.All(lines, l => Assert.True(TextWidth.VisibleWidth(l) <= 90, l));
        }
    }

    [Fact]
    public void ShufflesAfterTheInterval()
    {
        var sources = new DeviousSources(_t.Runtime, _project, seed: 1);
        var screen = new DeviousScreen(sources, seed: 5);
        screen.Render(120, 40);
        var before = screen.Layout.Select(l => l.Pane).ToList();

        var changed = false;
        for (var i = 0; i < 5 && !changed; i++)
        {
            screen.Advance(DeviousScreen.ShuffleInterval);
            screen.Render(120, 40);
            changed = !before.SequenceEqual(screen.Layout.Select(l => l.Pane));
        }

        Assert.True(changed);
    }

    [Fact]
    public void PanelRunsAndQLeaves()
    {
        using var panel = new DeviousPanel(new PanelContext { Pickle = _t.Runtime });

        var screen = SystemPanelRunner.RunAndDraw(panel, When(() => true, panel.NextFrame));

        Assert.Contains("devious", screen, StringComparison.Ordinal);
        Assert.NotEmpty(panel.Screen.Layout);
    }

    [Fact]
    public async Task CommandNeedsAnInteractiveTerminal()
    {
        var errors = new List<string>();
        var context = new PickleCommandContext
        {
            Pickle = _t.Runtime,
            WriteObject = _ => { },
            WriteHost = _ => { },
            WriteError = errors.Add,
            Confirm = (_, d) => d,
            Cwd = _project,
        };

        Assert.Equal(1, await new DeviousCommand().ExecuteAsync(context, [], CancellationToken.None));
        Assert.Contains("interactive", Assert.Single(errors), StringComparison.Ordinal);
    }
}
