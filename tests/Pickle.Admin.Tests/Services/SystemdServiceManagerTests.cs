using Pickle.Abstractions.Services;
using Pickle.Admin.Privilege;
using Pickle.Admin.Services;
using Pickle.Testing.Fakes;

namespace Pickle.Admin.Tests.Services;

public class SystemdServiceManagerTests
{
    private const string Units = """
          ssh.service                        loaded    active   running OpenBSD Secure Shell server
          cron.service                       loaded    active   running Regular background program processing daemon
        ● apache2.service                    loaded    failed   failed  The Apache HTTP Server
          getty@tty1.service                 loaded    active   running Getty on tty1
          ghost.service                      not-found inactive dead    ghost.service
          fstrim.service                     loaded    inactive dead    Discard unused blocks on filesystems from /etc/fstab
        """;

    private const string Files = """
        ssh.service                    enabled         enabled
        cron.service                   enabled         enabled
        apache2.service                disabled        enabled
        getty@.service                 enabled         enabled
        fstrim.service                 static          -
        bluetooth.service              masked          enabled
        """;

    private static (SystemdServiceManager Manager, FakeProgramRunner Runner) Create(bool root = false)
    {
        var runner = new FakeProgramRunner()
            .On("systemctl", "list-units", Units)
            .On("systemctl", "list-unit-files", Files)
            .On("systemctl", "restart", string.Empty)
            .On("sudo", "-n", string.Empty);
        return (new SystemdServiceManager(runner, new UnixPrivilegeService(runner, () => root, _ => null)), runner);
    }

    [Fact]
    public async Task MergesLoadedUnitsWithInstalledUnitFiles()
    {
        var (manager, _) = Create();

        var services = await manager.ListAsync(userScope: false);

        Assert.Equal(["apache2.service", "bluetooth.service", "cron.service", "fstrim.service", "getty@.service", "getty@tty1.service", "ssh.service"], services.Select(s => s.Id));
        var ssh = services.Single(s => s.Id == "ssh.service");
        Assert.Equal((ServiceState.Running, ServiceStartMode.Automatic), (ssh.State, ssh.StartMode));
        Assert.Equal(ServiceState.Failed, services.Single(s => s.Id == "apache2.service").State);
        Assert.Equal(ServiceStartMode.Disabled, services.Single(s => s.Id == "apache2.service").StartMode);
        Assert.Equal(ServiceStartMode.Manual, services.Single(s => s.Id == "fstrim.service").StartMode);
        Assert.Equal(ServiceStartMode.Disabled, services.Single(s => s.Id == "bluetooth.service").StartMode);
        Assert.DoesNotContain(services, s => s.Id == "ghost.service");
    }

    [Fact]
    public async Task SystemControlGoesThroughSudoWithOneFixedCommand()
    {
        var (manager, runner) = Create();

        var result = await manager.ControlAsync("ssh.service", ServiceAction.Restart, userScope: false);

        Assert.True(result.Success);
        Assert.Equal(["-n /usr/bin/systemctl restart -- ssh.service"], runner.CommandLines("sudo"));
    }

    [Fact]
    public async Task UserScopeNeverNeedsPrivileges()
    {
        var (manager, runner) = Create();
        runner.On("systemctl", "--user", string.Empty);

        var result = await manager.ControlAsync("pipewire.service", ServiceAction.Start, userScope: true);

        Assert.True(result.Success);
        Assert.Empty(runner.CommandLines("sudo"));
        Assert.Contains("--user start -- pipewire.service", runner.CommandLines("systemctl"));
    }

    [Fact]
    public async Task APasswordPromptHandsTheCommandBackForTheShell()
    {
        var runner = new FakeProgramRunner()
            .On("systemctl", "stop", string.Empty)
            .On("sudo", "-n", string.Empty, exitCode: 1, stderr: "sudo: a password is required");
        var manager = new SystemdServiceManager(runner, new UnixPrivilegeService(runner, () => false, _ => null));

        var result = await manager.ControlAsync("cron.service", ServiceAction.Stop, userScope: false);

        Assert.False(result.Success);
        Assert.Equal("sudo systemctl stop -- cron.service", result.ShellCommand);
    }

    [Theory]
    [InlineData("--now")]
    [InlineData("ssh")]
    [InlineData("a b.service")]
    [InlineData("$(x).service")]
    [InlineData("../x.service")]
    public async Task OnlyRealServiceNamesAreAccepted(string id)
    {
        var (manager, runner) = Create();

        await Assert.ThrowsAsync<ArgumentException>(() => manager.ControlAsync(id, ServiceAction.Start, userScope: false));
        Assert.Empty(runner.CommandLines("sudo"));
        Assert.Null(manager.LogsCommand(id, userScope: false));
    }

    [Fact]
    public void LogsCommandFollowsTheUnitInTheJournal() =>
        Assert.Equal("journalctl -f -n 100 -u ssh.service", Create().Manager.LogsCommand("ssh.service", userScope: false));
}
