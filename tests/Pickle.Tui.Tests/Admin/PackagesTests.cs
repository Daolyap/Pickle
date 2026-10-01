using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Testing;
using Pickle.Testing.Fakes;
using Pickle.Tui.Panels.Admin;
using Terminal.Gui.Input;

namespace Pickle.Tui.Tests.Admin;

public sealed class PackagesTests : IDisposable
{
    private readonly TestPickle _t = TestPickle.Create(start: true, plugins: [new AdminPanelsPlugin()]);
    private readonly FakeSystemPackageManager _pm = new();

    public PackagesTests()
    {
        _pm.Installed.AddRange(
        [
            new SystemPackage("bash", "5.1", "GNU shell"),
            new SystemPackage("openssl", "3.0.2", "TLS toolkit") { NewVersion = "3.0.18" },
        ]);
        _pm.Available.AddRange([new SystemPackage("ripgrep", string.Empty, "fast grep") { Installed = false }, new SystemPackage("bash", string.Empty, "GNU shell")]);
        _t.Runtime.Services.Add<ISystemPackageManager>(_pm);
    }

    public void Dispose() => _t.Dispose();

    [Fact]
    public void InstalledPackagesShowTheirVersionsAndUpgradesAreHighlighted()
    {
        var panel = new PackagesPanel(new PanelContext { Pickle = _t.Runtime });
        var script = new UiScript()
            .WaitFor("loaded", _ => panel.List.TotalCount == 2)
            .Do("hints", _ => Assert.Equal(["5.1", "3.0.2 → 3.0.18"], panel.List.VisibleItems.Select(p => p.Upgradable ? $"{p.Version} → {p.NewVersion}" : p.Version)))
            .Press(Key.F6)
            .WaitFor("upgrades", _ => panel.List.TotalCount == 1 && panel.List.Selected?.Name == "openssl")
            .Press(Key.F7);
        var context = new PanelContext { Pickle = _t.Runtime };
        var upgradePanel = new PackagesPanel(context);
        var upgradeScript = new UiScript()
            .WaitFor("loaded", _ => upgradePanel.List.TotalCount == 2)
            .Press(Key.F6)
            .WaitFor("upgrades", _ => upgradePanel.List.TotalCount == 1)
            .Press(Key.F7);

        TuiHarness.Run(upgradePanel, upgradeScript);
        TuiHarness.Run(panel, new UiScript().WaitFor("loaded", _ => panel.List.TotalCount == 2).Press(Key.Esc));

        upgradeScript.AssertOk();
        Assert.Equal(new PanelResult(PanelResultKind.RunCommand, "sudo fakepm upgrade openssl"), context.Result);
        _ = script;
    }

    [Fact]
    public void SearchResultsOfferInstallForWhatIsMissing()
    {
        var context = new PanelContext { Pickle = _t.Runtime, Argument = "ripgrep" };
        var panel = new PackagesPanel(context);
        var script = new UiScript()
            .WaitFor("results", _ => panel.List.TotalCount == 1 && panel.List.Selected?.Name == "ripgrep")
            .Press(Key.F3);

        TuiHarness.Run(panel, script);

        script.AssertOk();
        Assert.Equal(new PanelResult(PanelResultKind.RunCommand, "sudo fakepm install ripgrep"), context.Result);
    }

    [Fact]
    public void RemoveIsOnlyOfferedForInstalledPackagesAndInstallOnlyForMissingOnes()
    {
        var context = new PanelContext { Pickle = _t.Runtime };
        var panel = new PackagesPanel(context);
        var script = new UiScript()
            .WaitFor("loaded", _ => panel.List.TotalCount == 2 && panel.List.Selected?.Name == "bash")
            .Press(Key.F3)
            .Press(Key.F4);

        TuiHarness.Run(panel, script);

        script.AssertOk();
        Assert.Equal(new PanelResult(PanelResultKind.RunCommand, "sudo fakepm remove bash"), context.Result);
    }

    [Fact]
    public void CommandsListSearchAndRunThroughTheManager()
    {
        Assert.Equal(["openssl"], _t.Run("pk pkg upgrades | ForEach-Object Name"));
        Assert.Equal(["ripgrep"], _t.Run("pk pkg search ripgrep | Where-Object { -not $_.Installed } | ForEach-Object Name"));
        Assert.Equal(["bash"], _t.Run("pk pkg list bas | ForEach-Object Name"));

        _t.Run("pk pkg install ripgrep --yes");
        _t.Run("pk pkg upgrade-all --yes");

        Assert.Contains("Install ripgrep", _pm.Calls);
        Assert.Contains("UpgradeAll", _pm.Calls);
    }

    [Fact]
    public void BadPackageNamesNeverReachTheManager()
    {
        Assert.Throws<InvalidOperationException>(() => _t.Run("pk pkg install --yes 'ripgrep;calc'"));
        Assert.DoesNotContain(_pm.Calls, c => c.StartsWith("Install", StringComparison.Ordinal));
    }
}
