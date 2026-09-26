using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Testing;
using Pickle.Testing.Fakes;
using Pickle.Tui.Panels.Dashboard;
using Pickle.Tui.Tests.SystemMonitoring;
using static Pickle.Tui.Tests.Windows.PanelRunner;

namespace Pickle.Tui.Tests.Dashboard;

public sealed class DashboardTests : IDisposable
{
    private readonly TestPickle _t = TestPickle.Create();
    private readonly FakeProcessMonitor _processes = new() { CpuPercent = 37.5 };
    private readonly FakeNetworkMonitor _network = new();
    private readonly FakeDiskMonitor _disks = new();
    private readonly FakeSystemInfo _system = new();

    public DashboardTests()
    {
        var services = _t.Runtime.ServiceRegistry;
        services.Add<IProcessMonitor>(_processes);
        services.Add<INetworkMonitor>(_network);
        services.Add<IDiskMonitor>(_disks);
        services.Add<ISystemInfo>(_system);
        _processes.Processes.Add(new ProcessSample(10, "firefox", 22.5, 900L << 20, 80, null));
        _processes.Processes.Add(new ProcessSample(11, "code", 3.0, 1500L << 20, 40, null));
        _processes.Processes.Add(new ProcessSample(12, "pickle", 1.0, 150L << 20, 20, null));
    }

    public void Dispose() => _t.Dispose();

    private async Task<DashboardData> Sample()
    {
        var sampler = new DashboardSampler(_t.Runtime);
        await sampler.SampleAsync(CancellationToken.None);
        return await sampler.SampleAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WideLayoutShowsEveryTile()
    {
        var data = await Sample();

        var text = string.Join('\n', DashboardLayout.Render(data, 120, 40).PlainLines());

        foreach (var expected in new[]
        {
            "System", "CPU & memory", "Disks", "Network", "Busiest (CPU)", "Largest (memory)",
            "pickle-box (tester)", "Windows 11 Pro 24H2", "AMD Ryzen 7 5800X", "16 threads", "3d 4h 30m", "84% · charging",
            "needed for Windows Update", "37.5%", "4.00 GB / 16.0 GB", "6.00 GB / 24.0 GB", "3 processes",
        })
        {
            Assert.Contains(expected, text, StringComparison.Ordinal);
        }

        var lines = DashboardLayout.Render(data, 120, 40).PlainLines();
        Assert.Contains(lines, l => l.Contains("firefox", StringComparison.Ordinal) && l.Contains("22.5%", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("code", StringComparison.Ordinal) && l.Contains("1.46 GB", StringComparison.Ordinal));
        Assert.All(lines, l => Assert.True(TextWidth.VisibleWidth(l) <= 120, l));
    }

    [Fact]
    public async Task NarrowAndShortTerminalsStackTilesAndDropTheLeastImportant()
    {
        var data = await Sample();

        var narrow = string.Join('\n', DashboardLayout.Render(data, 70, 60).PlainLines());
        Assert.Contains("Busiest (CPU)", narrow, StringComparison.Ordinal);
        Assert.DoesNotContain("Largest (memory)", narrow, StringComparison.Ordinal);

        var wideButShort = string.Join('\n', DashboardLayout.Render(data, 120, 16).PlainLines());
        Assert.Contains("Network", wideButShort, StringComparison.Ordinal);
        Assert.DoesNotContain("Busiest (CPU)", wideButShort, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SamplerKeepsHistoriesAndSurvivesAFailingSource()
    {
        var sampler = new DashboardSampler(_t.Runtime);
        await sampler.SampleAsync(CancellationToken.None);
        _t.Runtime.ServiceRegistry.Add<ISystemInfo>(new ThrowingSystemInfo());

        var data = await sampler.SampleAsync(CancellationToken.None);

        Assert.Equal([37.5, 37.5], data.CpuHistory);
        Assert.Equal(2, data.MemoryHistory.Count);
        Assert.Equal("pickle-box", data.System?.MachineName);
    }

    [Fact]
    public async Task OnceModePrintsASnapshot()
    {
        var lines = new List<string>();
        var context = new PickleCommandContext
        {
            Pickle = _t.Runtime,
            WriteObject = _ => { },
            WriteHost = lines.Add,
            WriteError = e => lines.Add("error: " + e),
            Confirm = (_, d) => d,
            Cwd = _t.Home,
        };

        Assert.Equal(0, await new DashboardCommand().ExecuteAsync(context, ["--once"], CancellationToken.None));
        Assert.Contains(lines, l => TextWidth.StripAnsi(l).Contains("pickle-box (tester)", StringComparison.Ordinal));
        Assert.Equal(2, await new DashboardCommand().ExecuteAsync(context, ["--bogus"], CancellationToken.None));
    }

    [Fact]
    public void PanelDrawsLiveData()
    {
        using var panel = new DashboardPanel(new PanelContext { Pickle = _t.Runtime });

        var screen = SystemPanelRunner.RunAndDraw(panel, Wait(() => panel.Latest?.System is not null));

        Assert.Contains("CPU & memory", screen, StringComparison.Ordinal);
        Assert.Contains("pickle-box", screen, StringComparison.Ordinal);
        Assert.Contains("Esc to close", screen, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.5, "30m")]
    [InlineData(5.25, "5h 15m")]
    [InlineData(50, "2d 2h 0m")]
    public void UptimeIsCompact(double hours, string expected) => Assert.Equal(expected, DashboardLayout.Uptime(TimeSpan.FromHours(hours)));

    private sealed class ThrowingSystemInfo : ISystemInfo
    {
        public Task<SystemSummary> GetSummaryAsync(CancellationToken cancellationToken = default) => throw new IOException("nope");
    }
}
