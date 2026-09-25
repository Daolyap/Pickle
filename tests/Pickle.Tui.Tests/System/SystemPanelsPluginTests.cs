using Pickle.Abstractions;
using Pickle.Core.SystemMonitoring;
using Pickle.Testing;
using Pickle.Testing.Fakes;
using Pickle.Tui.Panels.SystemMonitoring;

namespace Pickle.Tui.Tests.SystemMonitoring;

public class SystemPanelsPluginTests
{
    [Fact]
    public void PanelsAreRegisteredWithTheirChordsAndListedInThePalette()
    {
        var (t, _) = TuiHarness.Start();
        using var _t = t;
        new SystemMonitorsPlugin().Initialize(t.Runtime);
        new SystemPanelsPlugin().Initialize(t.Runtime);

        Assert.Equal("panel.processes", t.Runtime.KeyBindings.Bindings["Alt+P"]);
        Assert.Equal("panel.network", t.Runtime.KeyBindings.Bindings["Alt+N"]);
        Assert.Equal("panel.disks", t.Runtime.KeyBindings.Bindings["Alt+D"]);
        var titles = Panels.Palette.PaletteItem.Collect(t.Runtime).Where(i => i.Kind == Panels.Palette.PaletteKind.Panel).Select(i => i.Title).ToList();
        Assert.Contains("Processes", titles);
        Assert.Contains("Network", titles);
        Assert.Contains("Disks", titles);
        Assert.NotNull(t.Runtime.Services.Get<Abstractions.Services.IProcessMonitor>());
        Assert.NotNull(t.Runtime.Services.Get<Abstractions.Services.INetworkMonitor>());
        Assert.NotNull(t.Runtime.Services.Get<Abstractions.Services.IDiskMonitor>());
    }

    [Fact]
    public void PkCommandsOpenTheirPanels()
    {
        using var t = TestPickle.Create(start: true, plugins: [new SystemPanelsPlugin()]);
        var host = new FakePanelHost();
        t.Runtime.ServiceRegistry.Add<IPanelHost>(host);
        var folder = TuiHarness.TempDir();
        try
        {
            t.Run("pk top");
            t.Run("pk top fire fox");
            t.Run("pk net");
            t.Run($"pk disks '{folder}'");

            Assert.Equal(
                [("processes", null), ("processes", "fire fox"), ("network", null), ("disks", folder)],
                host.Shown.Select(s => (s.PanelId, s.Argument)));
            Assert.Throws<InvalidOperationException>(() => t.Run($"pk disks '{Path.Combine(folder, "missing")}'"));
            Assert.Equal(4, host.Shown.Count);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
