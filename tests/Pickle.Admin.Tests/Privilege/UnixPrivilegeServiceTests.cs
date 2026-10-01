using Pickle.Abstractions.Services;
using Pickle.Admin.Privilege;
using Pickle.Testing.Fakes;

namespace Pickle.Admin.Tests.Privilege;

public class UnixPrivilegeServiceTests
{
    private static readonly PrivilegedCommand Restart = new("systemctl", ["restart", "--", "ssh.service"], "Restart ssh");

    [Fact]
    public async Task RootRunsTheProgramDirectly()
    {
        var runner = new FakeProgramRunner().On("systemctl", "restart", string.Empty);
        var service = new UnixPrivilegeService(runner, () => true, _ => null);

        var result = await service.RunAsync(Restart);

        Assert.True(result.Success);
        Assert.Equal(PrivilegeMethod.AlreadyPrivileged, service.Method);
        Assert.Equal(["restart -- ssh.service"], runner.CommandLines("systemctl"));
    }

    [Fact]
    public async Task SudoRunsNonInteractivelyAndAPasswordPromptBecomesATerminalRequest()
    {
        var runner = new FakeProgramRunner()
            .On("systemctl", "restart", string.Empty)
            .On("sudo", "-n", string.Empty, exitCode: 1, stderr: "sudo: a password is required");
        var service = new UnixPrivilegeService(runner, () => false, _ => null);

        var result = await service.RunAsync(Restart);

        Assert.False(result.Success);
        Assert.True(result.NeedsTerminal);
        Assert.Equal(["-n /usr/bin/systemctl restart -- ssh.service"], runner.CommandLines("sudo"));
        Assert.Equal("sudo systemctl restart -- ssh.service", service.ShellCommand(Restart));
    }

    [Fact]
    public async Task PkexecIsUsedInAGraphicalSession()
    {
        var runner = new FakeProgramRunner()
            .On("systemctl", "restart", string.Empty)
            .On("pkexec", "/usr/bin/systemctl", string.Empty)
            .On("sudo", "-n", string.Empty, exitCode: 1, stderr: "should not be used");
        var service = new UnixPrivilegeService(runner, () => false, name => name == "WAYLAND_DISPLAY" ? "wayland-0" : null);

        var result = await service.RunAsync(Restart);

        Assert.True(result.Success);
        Assert.Empty(runner.CommandLines("sudo"));
        Assert.Single(runner.CommandLines("pkexec"));
    }

    [Fact]
    public async Task WithoutAnyToolItSaysSoInsteadOfPretending()
    {
        var runner = new FakeProgramRunner().On("systemctl", "restart", string.Empty);
        var service = new UnixPrivilegeService(runner, () => false, _ => null);

        var result = await service.RunAsync(Restart);

        Assert.False(result.Success);
        Assert.False(result.NeedsTerminal);
        Assert.Equal(PrivilegeMethod.None, service.Method);
        Assert.Contains("sudo", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StandardInputReachesTheProgram()
    {
        var runner = new FakeProgramRunner().On("tee", (_, options) => new ProgramResult(options?.StandardInput == "data" ? 0 : 1, string.Empty, string.Empty));
        var service = new UnixPrivilegeService(runner, () => true, _ => null);

        var result = await service.RunAsync(new PrivilegedCommand("tee", ["/etc/x"], "write") { StandardInput = "data" });

        Assert.True(result.Success);
    }

    [Fact]
    public void ShellCommandsQuoteArgumentsSoTheyAreSafeToPasteIntoTheShell()
    {
        var service = new UnixPrivilegeService(new FakeProgramRunner(), () => false, _ => null);

        var text = service.ShellCommand(new PrivilegedCommand("apt-get", ["install", "-y", "a b", "it's"], "install"));

        Assert.Equal("sudo apt-get install -y 'a b' 'it''s'", text);
    }
}
