using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Testing;
using Pickle.Testing.Fakes;
using Pickle.Tui.Panels.Admin;
using Terminal.Gui.Input;

namespace Pickle.Tui.Tests.Admin;

public sealed class ServicesPanelTests : IDisposable
{
    private readonly TestPickle _t = TestPickle.Create();
    private readonly FakeSystemServiceManager _manager = new();

    public ServicesPanelTests()
    {
        _manager.System.AddRange(
        [
            new ServiceInfo("ssh.service", "OpenBSD Secure Shell server", ServiceState.Running, ServiceStartMode.Automatic),
            new ServiceInfo("cron.service", "Regular background program processing daemon", ServiceState.Stopped, ServiceStartMode.Automatic),
            new ServiceInfo("apache2.service", "The Apache HTTP Server", ServiceState.Failed, ServiceStartMode.Disabled),
        ]);
        _manager.User.Add(new ServiceInfo("pipewire.service", "PipeWire", ServiceState.Running, ServiceStartMode.Automatic));
        _t.Runtime.Services.Add<ISystemServiceManager>(_manager);
    }

    public void Dispose() => _t.Dispose();

    private (ServicesPanel Panel, PanelContext Context) Open(string? filter)
    {
        var context = new PanelContext { Pickle = _t.Runtime, Argument = filter };
        return (new ServicesPanel(context), context);
    }

    [Fact]
    public void ListsServicesAndFiltersByTheArgument()
    {
        var (panel, _) = Open("cron");
        var script = new UiScript()
            .WaitFor("loaded", _ => panel.List.TotalCount == 3 && panel.List.Selected?.Id == "cron.service")
            .WaitFor("details", _ => panel.Details.PlainText.Contains("details of cron.service", StringComparison.Ordinal))
            .Press(Key.Esc);

        TuiHarness.Run(panel, script);

        script.AssertOk();
    }

    [Fact]
    public void StartRunsImmediatelyForAStoppedService()
    {
        var (panel, _) = Open("cron");
        var script = new UiScript()
            .WaitFor("selected", _ => panel.List.Selected?.Id == "cron.service")
            .Press(Key.F2)
            .WaitFor("called", _ => _manager.Calls.Count == 1)
            .Press(Key.Esc);

        TuiHarness.Run(panel, script);

        script.AssertOk();
        Assert.Equal(["system Start cron.service"], _manager.Calls);
    }

    [Fact]
    public void StopAsksFirstAndOnlyActsOnAConfirmedYes()
    {
        var (panel, _) = Open("ssh");
        var script = new UiScript()
            .WaitFor("selected", _ => panel.List.Selected?.Id == "ssh.service")
            .Press(Key.F3)
            .WaitFor("dialog", app => app.TopRunnableView is Terminal.Gui.Views.Dialog)
            .Press(Key.Y)
            .WaitFor("called", _ => _manager.Calls.Count == 1)
            .Press(Key.Esc);

        TuiHarness.Run(panel, script);

        script.AssertOk();
        Assert.Equal(["system Stop ssh.service"], _manager.Calls);
    }

    [Fact]
    public void StopIsNotOfferedForAServiceThatIsNotRunning()
    {
        var (panel, _) = Open("cron");
        var script = new UiScript()
            .WaitFor("selected", _ => panel.List.Selected?.Id == "cron.service")
            .Press(Key.F3)
            .Press(Key.Esc);

        TuiHarness.Run(panel, script);

        script.AssertOk();
        Assert.Empty(_manager.Calls);
    }

    [Fact]
    public void APasswordPromptOffersToRunTheCommandInTheShell()
    {
        _manager.Result = new ServiceOperationResult(false, "A password is needed.") { ShellCommand = "sudo systemctl start -- cron.service" };
        var (panel, context) = Open("cron");
        var script = new UiScript()
            .WaitFor("selected", _ => panel.List.Selected?.Id == "cron.service")
            .Press(Key.F2)
            .WaitFor("dialog", app => app.TopRunnableView is Terminal.Gui.Views.Dialog)
            .Press(Key.Y);

        TuiHarness.Run(panel, script);

        script.AssertOk();
        Assert.Equal(new PanelResult(PanelResultKind.RunCommand, "sudo systemctl start -- cron.service"), context.Result);
    }

    [Fact]
    public void F8HandsTheLogCommandToTheShellAndF9SwitchesToUserServices()
    {
        var (panel, context) = Open(null);
        var script = new UiScript()
            .WaitFor("loaded", _ => panel.List.TotalCount == 3)
            .Press(Key.F9)
            .WaitFor("user scope", _ => panel.List.TotalCount == 1 && panel.List.Selected?.Id == "pipewire.service")
            .Press(Key.F8);

        TuiHarness.Run(panel, script);

        script.AssertOk();
        Assert.Equal(new PanelResult(PanelResultKind.RunCommand, "journalctl -f -u pipewire.service"), context.Result);
    }
}
