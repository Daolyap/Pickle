using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Testing;
using Pickle.Testing.Fakes;
using Pickle.Tui.Panels.Admin;
using Pickle.Tui.Panels.Admin.EnvEditor;
using Terminal.Gui.Input;

namespace Pickle.Tui.Tests.Admin;

public sealed class EnvironmentPanelTests : IDisposable
{
    private readonly TestPickle _t = TestPickle.Create();
    private readonly FakeHostsService _hosts = new() { Text = "# my hosts\n127.0.0.1 localhost\n10.0.0.5 web web.lan  # dev box\n" };
    private readonly FakeEnvironmentStore _env = new();

    // Real folders and full paths on every OS: the PATH editor checks them against the file system ("/usr/bin" is not a full path on Windows).
    private static readonly string Existing = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    private static readonly string Missing = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "definitely", "not", "here");

    public EnvironmentPanelTests()
    {
        _t.Runtime.Services.Add<IHostsService>(_hosts);
        _t.Runtime.Services.Add<IEnvironmentStore>(_env);
        _env.Path(EnvironmentScope.User).AddRange([Existing, Missing, Existing]);
        _env.Values(EnvironmentScope.User)["EDITOR"] = "nano";
    }

    public void Dispose() => _t.Dispose();

    private (EnvironmentPanel Panel, PanelContext Context) Open(string argument, Func<string, string, string?>? prompt = null, bool confirm = true)
    {
        var context = new PanelContext { Pickle = _t.Runtime, Argument = argument };
        var panel = new EnvironmentPanel(context) { PromptHook = prompt, ConfirmHook = (_, _) => confirm };
        return (panel, context);
    }

    [Fact]
    public void HostsAreListedWithDisabledAndDuplicateMarkers()
    {
        _hosts.Text += "10.0.0.6 web\n# 10.0.0.7 old\n";
        var (panel, _) = Open("hosts");
        var script = new UiScript()
            .WaitFor("loaded", _ => panel.List.TotalCount == 4)
            .Do("check", _ =>
            {
                Assert.Equal([null, "disabled", "duplicate", "duplicate"], panel.List.VisibleItems.Select(r => r.Hint).Order().ToArray());
            })
            .Press(Key.Esc);

        TuiHarness.Run(panel, script);

        script.AssertOk();
    }

    [Fact]
    public void AddingAHostStagesADiffAndSaveWritesOneFileThroughTheService()
    {
        var (panel, _) = Open("hosts", prompt: (_, _) => "10.0.0.9 nas # file server");
        var script = new UiScript()
            .WaitFor("loaded", _ => panel.List.TotalCount == 2)
            .Press(Key.F2)
            .WaitFor("staged", _ => panel.List.TotalCount == 3 && panel.Mode.IsDirty)
            .WaitFor("diff", _ => panel.Details.PlainText.Contains("+ 10.0.0.9", StringComparison.Ordinal))
            .Press(Key.F10)
            .WaitFor("written", _ => _hosts.Written.Count == 1)
            .Press(Key.Esc);

        TuiHarness.Run(panel, script);

        script.AssertOk();
        Assert.Equal("# my hosts\n127.0.0.1 localhost\n10.0.0.5 web web.lan  # dev box\n10.0.0.9        nas  # file server\n", _hosts.Written[0]);
    }

    [Fact]
    public void ToggleDisablesAnEntryAndDeleteRemovesIt()
    {
        var (panel, _) = Open("hosts");
        var script = new UiScript()
            .WaitFor("loaded", _ => panel.List.TotalCount == 2)
            .WaitFor("web selected", _ => panel.List.Selected?.Text.Contains("web", StringComparison.Ordinal) == true || panel.List.VisibleItems.Count == 2)
            .Do("select web", _ => panel.List.Select(panel.List.VisibleItems.First(r => r.Text.Contains("web", StringComparison.Ordinal))))
            .Press(Key.F6)
            .WaitFor("disabled", _ => panel.List.VisibleItems.Any(r => r.Hint == "disabled"))
            .Press(Key.F4)
            .WaitFor("removed", _ => panel.List.TotalCount == 1)
            .Press(Key.F10)
            .WaitFor("written", _ => _hosts.Written.Count == 1)
            .Press(Key.Esc);

        TuiHarness.Run(panel, script);

        script.AssertOk();
        Assert.Equal("# my hosts\n127.0.0.1 localhost\n", _hosts.Written[0]);
    }

    [Fact]
    public void AnInvalidHostsLineIsRefusedWithAMessageAndNothingIsStaged()
    {
        var (panel, _) = Open("hosts", prompt: (_, _) => "not an entry");
        var script = new UiScript()
            .WaitFor("loaded", _ => panel.List.TotalCount == 2)
            .Press(Key.F2)
            .WaitFor("message", _ => panel.Details.PlainText.Contains("Expected: 10.0.0.5", StringComparison.Ordinal))
            .Press(Key.Esc);

        TuiHarness.Run(panel, script);

        script.AssertOk();
        Assert.False(panel.Mode.IsDirty);
    }

    [Fact]
    public void PathEntriesAreFlaggedReorderedAndSavedOnceInTheChosenScope()
    {
        var (panel, _) = Open("path");
        var script = new UiScript()
            .WaitFor("loaded", _ => panel.List.TotalCount == 3)
            .Do("flags", _ => Assert.Equal([Existing + ":", Missing + ":missing", Existing + ":duplicate"], panel.List.VisibleItems.Select(r => r.Text + ":" + r.Hint).ToArray()))
            .Do("select the missing entry", _ => panel.List.Select(panel.List.VisibleItems[1]))
            .Press(Key.F4)
            .WaitFor("removed", _ => panel.List.TotalCount == 2 && panel.Mode.IsDirty)
            .Press(Key.F10)
            .WaitFor("saved", _ => _env.Calls.Count == 1)
            .Press(Key.Esc);

        TuiHarness.Run(panel, script);

        script.AssertOk();
        Assert.Equal([$"path User {Existing}|{Existing}"], _env.Calls);
    }

    [Fact]
    public void VariablesChangeImmediatelyAndTheLiveProcessIsReadOnly()
    {
        var (panel, _) = Open("variables", prompt: (_, label) => label == "NAME=value" ? "PAGER=less" : null);
        var script = new UiScript()
            .WaitFor("loaded", _ => panel.List.TotalCount == 1)
            .Press(Key.F2)
            .WaitFor("added", _ => panel.List.TotalCount == 2)
            .Do("process scope", _ => panel.SelectMode(2))
            .Press(Key.Esc);

        TuiHarness.Run(panel, script);

        script.AssertOk();
        Assert.Equal(["set User PAGER=less"], _env.Calls);
        Assert.Equal("less", _env.Values(EnvironmentScope.User)["PAGER"]);
    }

    [Fact]
    public void DotEnvEditsTheFileInTheCurrentFolderOnlyOnSave()
    {
        var dir = Directory.CreateTempSubdirectory("pickle-dotenv").FullName;
        var file = Path.Combine(dir, ".env");
        File.WriteAllText(file, "# app\nPORT=8080\n");
        try
        {
            var (panel, _) = Open("dotenv " + file, prompt: (_, label) => label == "NAME=value" ? "DEBUG=1" : null);
            var script = new UiScript()
                .WaitFor("loaded", _ => panel.List.TotalCount == 1)
                .Press(Key.F2)
                .WaitFor("staged", _ => panel.List.TotalCount == 2 && panel.Mode.IsDirty)
                .Do("untouched until save", _ => Assert.Equal("# app\nPORT=8080\n", File.ReadAllText(file)))
                .Press(Key.F10)
                .WaitFor("saved", _ => !panel.Mode.IsDirty)
                .Press(Key.Esc);

            TuiHarness.Run(panel, script);

            script.AssertOk();
            Assert.Equal("# app\nPORT=8080\nDEBUG=1\n", File.ReadAllText(file));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
