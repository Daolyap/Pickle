using Pickle.Abstractions.Services;
using Pickle.Testing;
using Pickle.Testing.Fakes;
using Pickle.Tui.Panels.Admin;

namespace Pickle.Tui.Tests.Admin;

public class ServicesCommandTests
{
    private static (TestPickle T, FakeSystemServiceManager Manager) Start()
    {
        var t = TestPickle.Create(start: true, plugins: [new AdminPanelsPlugin()]);
        var manager = new FakeSystemServiceManager();
        manager.System.AddRange(
        [
            new ServiceInfo("ssh.service", "Secure Shell", ServiceState.Running, ServiceStartMode.Automatic),
            new ServiceInfo("apache2.service", "Apache", ServiceState.Failed, ServiceStartMode.Disabled),
        ]);
        t.Runtime.Services.Add<ISystemServiceManager>(manager);
        return (t, manager);
    }

    [Fact]
    public void ListFiltersByStateAndText()
    {
        var (t, _) = Start();
        using var _ = t;

        Assert.Equal(["apache2.service"], t.Run("pk services list --state failed | ForEach-Object Id"));
        Assert.Equal(["ssh.service"], t.Run("pk services list ssh | ForEach-Object Id"));
        Assert.Equal(["apache2.service", "ssh.service"], t.Run("pk services list | ForEach-Object Id").Order());
    }

    [Fact]
    public void ActionsRunOneControlCallAndDestructiveOnesNeedYes()
    {
        var (t, manager) = Start();
        using var _ = t;

        t.Run("pk services start ssh.service");
        t.Run("pk services restart ssh.service --yes");
        t.Terminal.Type("n").Press("Enter");
        t.Run("pk services stop ssh.service");

        Assert.Equal(["system Start ssh.service", "system Restart ssh.service"], manager.Calls);
    }

    [Fact]
    public void ShowsTheShellCommandWhenAPasswordIsNeeded()
    {
        var (t, manager) = Start();
        using var _ = t;
        manager.Result = new ServiceOperationResult(false, "A password is needed.") { ShellCommand = "sudo systemctl start -- ssh.service" };

        t.Run("pk services start ssh.service");

        Assert.Contains("sudo systemctl start -- ssh.service", t.Terminal.GetScreenText(), StringComparison.Ordinal);
    }
}
