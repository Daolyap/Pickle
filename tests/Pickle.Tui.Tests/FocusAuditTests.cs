using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Testing;
using Pickle.Testing.Fakes;
using Terminal.Gui.ViewBase;

namespace Pickle.Tui.Tests;

public class FocusAuditTests
{
    [Fact]
    public void NoPanelLocksFocusableViewsInsideAnUnfocusableContainer()
    {
        using var t = TestPickle.Create(start: true, plugins:
        [
            new TuiPlugin(), new Panels.Git.GitPanelPlugin(), new Panels.Windows.WindowsPanelsPlugin(), new Panels.Wizard.WizardPanelPlugin(),
            new Panels.SystemMonitoring.SystemPanelsPlugin(), new Panels.NetTools.NetToolsPanelPlugin(), new Panels.Dashboard.DashboardPanelPlugin(),
            new Panels.Themes.ThemeGalleryPanelPlugin(), new Panels.Ssh.SshPanelPlugin(),
        ]);
        t.Runtime.ServiceRegistry.Add<IWingetService>(new FakeWingetService());
        t.Runtime.ServiceRegistry.Add<IWindowsUpdateService>(new FakeWindowsUpdateService());
        t.Runtime.ServiceRegistry.Add<ITaskSchedulerService>(new FakeTaskSchedulerService());
        var log = new List<string>();
        using var app = TuiHarness.InitApp();
        foreach (var panel in t.Runtime.Panels.All)
        {
            try
            {
                var view = (View)panel.CreateView(new PanelContext { Pickle = t.Runtime })!;
                Walk(view, panel.Id, log, view.GetType().Name);
                (view as IDisposable)?.Dispose();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                log.Add($"{panel.Id}: could not create ({ex.GetType().Name}: {ex.Message})");
            }
        }

        // A container left at CanFocus = false makes the text boxes inside it unclickable (FocusSync opens these at
        // run time as a safety net; this keeps the built-in panels right to begin with).
        Assert.Empty(log);
    }

    private static bool Walk(View view, string id, List<string> log, string path)
    {
        var holds = false;
        foreach (var child in view.SubViews)
        {
            holds |= Walk(child, id, log, path + "/" + child.GetType().Name + (child.Title is { Length: > 0 } title ? $"[{title}]" : string.Empty)) || child.CanFocus;
        }

        if (holds && !view.CanFocus)
        {
            log.Add($"{id}: {path}");
        }

        return holds;
    }
}
