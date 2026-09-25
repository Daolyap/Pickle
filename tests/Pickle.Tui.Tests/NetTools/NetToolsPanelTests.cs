using System.Net;
using System.Net.Sockets;
using Pickle.Abstractions;
using Pickle.Testing;
using Pickle.Tui.Panels.NetTools;
using Pickle.Tui.Tests.SystemMonitoring;
using static Pickle.Tui.Tests.Windows.PanelRunner;

namespace Pickle.Tui.Tests.NetTools;

public sealed class NetToolsPanelTests : IDisposable
{
    private readonly TestPickle _t = TestPickle.Create();

    public void Dispose() => _t.Dispose();

    [Fact]
    public void ArgumentPicksTheToolAndItsFirstField()
    {
        using var panel = Create("subnet 10.9.8.7/22");

        Assert.Equal("subnet", panel.ToolId);
        Assert.Equal("10.9.8.7/22", panel.Values()["cidr"]);
        Assert.Equal("pk subnet 10.9.8.7/22", panel.CommandLine);
    }

    [Fact]
    public void RunsAToolAndShowsItsLines()
    {
        using var panel = Create("subnet 10.0.0.0/24");
        var screen = SystemPanelRunner.RunAndDraw(
            panel,
            Do(() =>
            {
                panel.SetField("split", "26");
                panel.Run();
            }),
            Wait(() => !panel.IsBusy && panel.StatusText.StartsWith("done", StringComparison.Ordinal)));

        Assert.Contains("Network:    10.0.0.0/24  (private (RFC 1918))", panel.Lines);
        Assert.Contains(panel.Lines, l => l.StartsWith("10.0.0.192/26", StringComparison.Ordinal));
        Assert.Contains("Results", screen, StringComparison.Ordinal);
        Assert.Contains("╭", screen, StringComparison.Ordinal);
        Assert.Contains("Port scan", screen, StringComparison.Ordinal);
    }

    [Fact]
    public void ScanFindsALoopbackListener()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var panel = Create("scan 127.0.0.1");
        Run(
            panel,
            Do(() =>
            {
                panel.SetField("ports", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
                panel.SetField("banner", "false");
                panel.Run();
            }),
            Wait(() => !panel.IsBusy && panel.StatusText.StartsWith("done", StringComparison.Ordinal)));

        Assert.Contains(panel.Lines, l => l.StartsWith("127.0.0.1", StringComparison.Ordinal) && l.Contains($"{port}/tcp", StringComparison.Ordinal) && l.Contains("open", StringComparison.Ordinal));
        Assert.Contains(panel.Lines, l => l.StartsWith("1 open port(s)", StringComparison.Ordinal));
    }

    [Fact]
    public void ErrorsAreShownInTheResults()
    {
        using var panel = Create("scan 10.0.0.0/8");
        Run(panel, Do(panel.Run), Wait(() => !panel.IsBusy && panel.StatusText == "failed"));

        Assert.Contains(panel.Lines, l => l.StartsWith("✖", StringComparison.Ordinal) && l.Contains("more than", StringComparison.Ordinal));
    }

    [Fact]
    public void F8PutsTheEquivalentCommandOnThePrompt()
    {
        var context = new PanelContext { Pickle = _t.Runtime, Argument = "scan 192.168.1.0/24" };
        using var panel = new NetToolsPanel(context);
        panel.SetField("ports", "web");
        panel.SetField("all", "true");

        panel.InsertCommand();

        Assert.Equal(new PanelResult(PanelResultKind.ReplaceInput, "pk scan 192.168.1.0/24 -p web --timeout 800 --banner --all"), context.Result);
    }

    private NetToolsPanel Create(string? argument) => new(new PanelContext { Pickle = _t.Runtime, Argument = argument });
}
