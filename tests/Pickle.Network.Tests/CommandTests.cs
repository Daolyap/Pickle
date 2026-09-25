using System.Net;
using System.Net.Sockets;
using Pickle.Abstractions;
using Pickle.Testing;

namespace Pickle.Network.Tests;

public sealed class CommandTests
{
    [Fact]
    public void PluginRegistersEveryCommand()
    {
        using var t = TestPickle.Create(start: true, plugins: [new NetworkToolsPlugin()]);
        foreach (var name in new[] { "scan", "sweep", "dns", "trace", "whois", "cert", "subnet", "http", "wol", "ip" })
        {
            Assert.NotNull(t.Runtime.CommandRegistry.Get(name));
        }
    }

    [Fact]
    public void ScanEmitsRowsThatScriptsCanUse()
    {
        using var t = TestPickle.Create(start: true, plugins: [new NetworkToolsPlugin()]);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var rows = t.Run($"pk scan 127.0.0.1 -p {port} | ForEach-Object {{ \"$($_.Host):$($_.Port):$($_.State)\" }}");

        Assert.Equal([$"127.0.0.1:{port}:Open"], rows);
    }

    [Fact]
    public void RowsUseTheTableViews()
    {
        using var t = TestPickle.Create(width: 120, start: true, plugins: [new NetworkToolsPlugin()]);

        var table = string.Join('\n', t.Run("pk subnet 10.0.0.0/24 --split /25 | Out-String -Width 120"));

        Assert.Contains("Cidr", table, StringComparison.Ordinal);
        Assert.Contains("10.0.0.128/25", table, StringComparison.Ordinal);
        Assert.DoesNotContain("Cidr      :", table, StringComparison.Ordinal);
        Assert.Equal(["True"], t.Run("(pk subnet 10.20.0.0/16).Kind -eq 'private (RFC 1918)'"));
    }

    [Fact]
    public async Task BadArgumentsAreUsageErrors()
    {
        using var t = TestPickle.Create(start: true, plugins: [new NetworkToolsPlugin()]);
        var result = await t.Runtime.Shell.InvokeAsync("pk scan 10.0.0.0/8");
        Assert.Contains(result.Errors, e => e.ToString().Contains("more than 65,536 hosts", StringComparison.Ordinal));
        result = await t.Runtime.Shell.InvokeAsync("pk subnet 10.0.0.0/33");
        Assert.Contains(result.Errors, e => e.ToString().Contains("prefix must be 0", StringComparison.Ordinal));
    }
}
