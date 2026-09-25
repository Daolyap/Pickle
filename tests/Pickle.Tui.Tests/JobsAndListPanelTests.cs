using Pickle.Abstractions;
using Pickle.Tui.Panels.Jobs;
using Pickle.Tui.Panels.ListPanel;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace Pickle.Tui.Tests;

public class JobsPanelTests
{
    // Start-Job needs a pwsh executable and ThreadJob isn't bundled in tests, so event jobs stand in for real ones.
    private const string StartEventJob = "Register-EngineEvent -SourceIdentifier PickleJobsTest -Action { 'tick' } | Out-Null";

    [Fact]
    public void ListsJobsAndReceiveRunsReceiveJob()
    {
        var script = new UiScript()
            .WaitFor("listed", app => TuiHarness.Top<JobsPanel>(app).Jobs.Any(j => j.Name == "PickleJobsTest"))
            .Do("select", app =>
            {
                var panel = TuiHarness.Top<JobsPanel>(app);
                panel.Select(panel.Jobs.First(j => j.Name == "PickleJobsTest").Id);
            })
            .WaitFor("output preview", app => TuiHarness.Top<JobsPanel>(app).Output.Title.StartsWith("Output of job", StringComparison.Ordinal))
            .Press(Key.Enter);
        var (t, host) = TuiHarness.Start(script);
        using var _ = t;
        t.Run(StartEventJob);
        var id = t.Run("(Get-Job -Name PickleJobsTest).Id").Single();

        var result = host.Show(JobsPanelPlugin.PanelId);

        script.AssertOk();
        Assert.Equal(new PanelResult(PanelResultKind.RunCommand, $"Receive-Job -Id {id} -Keep"), result);
    }

    [Fact]
    public void RemoveAsksForConfirmationThenRemovesTheJob()
    {
        var script = new UiScript()
            .WaitFor("listed", app => TuiHarness.Top<JobsPanel>(app).Jobs.Count == 1)
            .Press(Key.Delete)
            .WaitFor("confirm", app => app.TopRunnableView is Dialog)
            .Press(Key.Y)
            .WaitFor("removed", app => app.TopRunnableView is JobsPanel { Jobs.Count: 0 })
            .Press(Key.Esc);
        var (t, host) = TuiHarness.Start(script);
        using var _ = t;
        t.Run(StartEventJob);

        host.Show(JobsPanelPlugin.PanelId);

        script.AssertOk();
        Assert.Equal(["0"], t.Run("@(Get-Job).Count"));
    }

    [Fact]
    public void NewJobPassesTheCommandAsAParameter()
    {
        var script = new UiScript()
            .WaitFor("loaded", app => app.TopRunnableView is JobsPanel)
            .Press(Key.F2)
            .WaitFor("prompt", app => app.TopRunnableView is Dialog)
            .Type("'it''s'; $x")
            .Press(Key.Enter)
            .WaitFor("started", app => app.TopRunnableView is JobsPanel panel && panel.Jobs.Any(j => j.Name == "FakeThreadJob"))
            .Press(Key.Esc);
        var (t, host) = TuiHarness.Start(script);
        using var _ = t;
        t.Run("""
            function global:Start-ThreadJob {
                param([scriptblock]$ScriptBlock)
                $global:PickleNewJobText = $ScriptBlock.ToString()
                Register-EngineEvent -SourceIdentifier FakeThreadJob -Action $ScriptBlock
            }
            """);

        host.Show(JobsPanelPlugin.PanelId);

        script.AssertOk();
        Assert.Equal(["'it''s'; $x"], t.Run("$global:PickleNewJobText"));
    }
}

public class ListPanelTests
{
    private static ListPanelSpec Spec() => new()
    {
        Id = "fruit",
        Title = "Fruit",
        DefaultKey = "Alt+F9",
        ItemsScript = "'apple', 'banana' | ForEach-Object { [pscustomobject]@{ Name = $_; Length = $_.Length } }",
        Actions = new Dictionary<string, string>
        {
            ["Insert"] = "@{ Insert = $_.Name.ToUpper() }",
            ["Describe"] = "\"$($_.Name) has $($_.Length) letters\"",
        },
    };

    [Fact]
    public void SyncRegistersDescriptorsOnceAndAgainWhenReplaced()
    {
        var (t, host) = TuiHarness.Start();
        using var _ = t;
        var sync = host.ListPanels!;
        var spec = Spec();
        t.Runtime.Panels.RegisterList(spec);

        Assert.Equal(1, sync.Sync());
        Assert.Equal(0, sync.Sync());
        Assert.Equal("Fruit", t.Runtime.Panels.Get("fruit")?.Title);
        Assert.Equal("panel.fruit", t.Runtime.KeyBindings.Bindings["Alt+F9"]);

        t.Runtime.Panels.RegisterList(Spec());
        Assert.Equal(1, sync.Sync());
    }

    [Fact]
    public void ShowsItemsAndRunsTheFirstActionOnEnter()
    {
        var script = new UiScript()
            .WaitFor("loaded", app => TuiHarness.Top<PluginListPanel>(app).List.TotalCount == 2)
            .Type("ban")
            .WaitFor("filtered", app => TuiHarness.Top<PluginListPanel>(app).List.Selected?.Display == "banana")
            .WaitFor("details", app => TuiHarness.Top<PluginListPanel>(app).Preview.PlainText.Contains("Length : 6", StringComparison.Ordinal))
            .Press(Key.Enter);
        var (t, host) = TuiHarness.Start(script);
        using var _ = t;
        t.Runtime.Panels.RegisterList(Spec());

        var result = host.Show("fruit");

        script.AssertOk();
        Assert.Equal(new PanelResult(PanelResultKind.InsertText, "BANANA"), result);
    }

    [Fact]
    public void OtherActionsShowTheirOutput()
    {
        var script = new UiScript()
            .WaitFor("loaded", app => TuiHarness.Top<PluginListPanel>(app).List.TotalCount == 2)
            .Press(Key.F3)
            .WaitFor("output", app => TuiHarness.Top<PluginListPanel>(app).Preview.PlainText.Contains("apple has 5 letters", StringComparison.Ordinal))
            .Press(Key.Esc);
        var (t, host) = TuiHarness.Start(script);
        using var _ = t;
        t.Runtime.Panels.RegisterList(Spec());

        Assert.Null(host.Show("fruit"));
        script.AssertOk();
    }

    [Fact]
    public void CustomPreviewAndAutoRefresh()
    {
        var script = new UiScript()
            .WaitFor("preview", app => TuiHarness.Top<PluginListPanel>(app).Preview.PlainText.Contains("APPLE!", StringComparison.Ordinal))
            .WaitFor("refreshed", app => TuiHarness.Top<PluginListPanel>(app).List.TotalCount == 3)
            .Press(Key.Esc);
        var (t, host) = TuiHarness.Start(script);
        using var _ = t;
        t.Run("$global:fruitCount = 2");
        t.Runtime.Panels.RegisterList(new ListPanelSpec
        {
            Id = "live",
            Title = "Live",
            ItemsScript = "$global:fruitCount++; 1..$([Math]::Min($global:fruitCount, 3)) | ForEach-Object { 'apple' }",
            PreviewScript = "$_.ToUpper() + '!'",
            RefreshSeconds = 1,
        });

        Assert.Null(host.Show("live"));
        script.AssertOk();
    }

    [Fact]
    public void RegisterPicklePanelTakesPreviewRefreshAndACommand()
    {
        var (t, _) = TuiHarness.Start();
        using var __ = t;

        t.Run("Register-PicklePanel -Id todo -Title Todo -Items { 'a' } -Preview { 'details' } -RefreshSeconds 5 -Command todo");

        var spec = Assert.Single(t.Runtime.Panels.ListPanels, p => p.Id == "todo");
        Assert.Equal((" 'details' ", 5), (spec.PreviewScript, spec.RefreshSeconds));
        Assert.Equal("pk todo [argument]", t.Runtime.CommandRegistry.Get("todo")?.Usage);
        Assert.Throws<InvalidOperationException>(() => t.Run("Register-PicklePanel -Id other -Title Other -Items { 'b' } -Command config"));
    }
}
