using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Testing;
using Pickle.Testing.Fakes;
using Pickle.Tui.Panels.Windows;
using Pickle.Tui.Tests.SystemMonitoring;
using static Pickle.Tui.Tests.Windows.PanelRunner;

namespace Pickle.Tui.Tests.Windows;

public sealed class SandboxPanelTests : IDisposable
{
    private readonly TestPickle _t = TestPickle.Create();
    private readonly FakeSandboxService _sandbox = new();
    private readonly List<(string Title, string Message)> _told = [];

    public SandboxPanelTests() => _t.Runtime.ServiceRegistry.Add<ISandboxService>(_sandbox);

    public void Dispose() => _t.Dispose();

    [Fact]
    public void ListsPresetsAndSavedSetupsAndShowsTheFeatureStatus()
    {
        _sandbox.Saved.Add(new SandboxConfig { Name = "Mine", Networking = SandboxSwitch.Enable });
        using var panel = Create();

        var screen = SystemPanelRunner.RunAndDraw(panel, Wait(() => panel.StatusText.Length > 0));

        Assert.Equal(["◆ Safe browsing", "◆ Offline analysis", "● Mine"], panel.Entries);
        Assert.Equal("Safe browsing", panel.Working.Name);
        Assert.Contains("Windows Sandbox is on", panel.StatusText, StringComparison.Ordinal);
        Assert.Contains("Networking", screen, StringComparison.Ordinal);
        Assert.Contains("╭", screen, StringComparison.Ordinal);
    }

    [Fact]
    public void FeatureOffIsExplained()
    {
        _sandbox.Status = new SandboxStatus(true, false, false, null);
        using var panel = Create();

        Run(panel, Wait(() => panel.StatusText.Length > 0));

        Assert.Contains("F9 turns it on", panel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void LaunchUsesTheEditedCopyAndLeavesThePresetAlone()
    {
        using var panel = Create();
        Run(
            panel,
            Do(() =>
            {
                panel.Select("Offline analysis");
                panel.Working.DarkMode = true;
                panel.Launch();
            }),
            Wait(() => _sandbox.Launched.Count == 1));

        var launched = Assert.Single(_sandbox.Launched);
        Assert.Equal(("Offline analysis", true, SandboxSwitch.Disable), (launched.Name, launched.DarkMode, launched.Networking));
        Assert.False(_sandbox.Presets[1].DarkMode);
        Assert.Contains("Starting sandbox 'Offline analysis'", panel.LastMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidSetupIsReportedInsteadOfLaunched()
    {
        using var panel = Create();
        Run(
            panel,
            Do(() =>
            {
                panel.Select("Offline analysis");
                panel.Working.WingetPackages.Add("Git.Git");
                panel.Launch();
            }),
            Wait(() => _told.Count > 0));

        Assert.Empty(_sandbox.Launched);
        Assert.Contains(_told, t => t.Message.Contains("needs networking", StringComparison.Ordinal));
    }

    [Fact]
    public void SavingAPresetAsksForANewNameAndListsIt()
    {
        using var panel = Create();
        panel.PromptHook = (_, _) => "Browsing at work";
        Run(
            panel,
            Do(() =>
            {
                panel.Working.StartUrl = "https://intranet.example";
                panel.Save();
            }));

        var saved = Assert.Single(_sandbox.Saved);
        Assert.Equal(("Browsing at work", "https://intranet.example"), (saved.Name, saved.StartUrl));
        Assert.Contains("● Browsing at work", panel.Entries);
        Assert.Equal("Browsing at work", panel.Working.Name);
    }

    [Fact]
    public void DeleteOnlyRemovesSavedSetups()
    {
        _sandbox.Saved.Add(new SandboxConfig { Name = "Old" });
        using var panel = Create();
        Run(
            panel,
            Do(() =>
            {
                panel.Delete();
                panel.Select("Old");
                panel.Delete();
            }));

        Assert.Contains(_told, t => t.Message.Contains("Only saved setups", StringComparison.Ordinal));
        Assert.Empty(_sandbox.Saved);
        Assert.DoesNotContain("● Old", panel.Entries);
    }

    [Fact]
    public void PreviewShowsTheWsbSetupScriptAndProblems()
    {
        using var panel = Create();
        panel.Select("Offline analysis");
        panel.Working.DarkMode = true;
        panel.Working.WingetPackages.Add("Git.Git");

        var preview = panel.PreviewText();

        Assert.Contains("needs networking", preview, StringComparison.Ordinal);
        Assert.Contains("<Networking>Disable</Networking>", preview, StringComparison.Ordinal);
        Assert.Contains("# dark mode", preview, StringComparison.Ordinal);
    }

    [Fact]
    public void TurningTheFeatureOnGoesThroughTheService()
    {
        using var panel = Create();
        Run(panel, Do(panel.EnableFeature), Wait(() => _sandbox.EnableCalls == 1 && panel.LastMessage is not null));

        Assert.Contains("Restart your PC", panel.LastMessage, StringComparison.Ordinal);
    }

    private SandboxPanel Create()
    {
        var panel = new SandboxPanel(new PanelContext { Pickle = _t.Runtime })
        {
            ConfirmHook = (_, _) => true,
            MessageHook = (title, message) => _told.Add((title, message)),
        };
        return panel;
    }
}
