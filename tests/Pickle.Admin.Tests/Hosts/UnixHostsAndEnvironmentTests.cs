using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Admin.EnvVars;
using Pickle.Admin.Hosts;
using Pickle.Admin.Privilege;
using Pickle.Testing;
using Pickle.Testing.Fakes;

namespace Pickle.Admin.Tests.Hosts;

public sealed class UnixHostsAndEnvironmentTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pickle-admin").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private (UnixHostsService Service, FakeProgramRunner Runner, string Hosts) Hosts(Func<bool>? root = null, bool password = false)
    {
        var hosts = Path.Combine(_dir, "hosts");
        File.WriteAllText(hosts, "127.0.0.1 localhost\n");
        var runner = new FakeProgramRunner()
            .On("cp", "-p", string.Empty)
            .On("install", "-m", string.Empty)
            .On("sudo", "-n", string.Empty, exitCode: password ? 1 : 0, stderr: password ? "sudo: a password is required" : string.Empty);
        var privilege = new UnixPrivilegeService(runner, root ?? (() => false), _ => null);
        return (new UnixHostsService(privilege, hosts, Path.Combine(_dir, "stage")), runner, hosts);
    }

    [Fact]
    public async Task WritingStagesTheFileAndInstallsItWithOnePrivilegedCommandAfterABackup()
    {
        var (service, runner, hosts) = Hosts();
        var document = await service.ReadAsync();
        document.Add(new HostsEntry("10.0.0.9", ["nas"], null, true));

        var result = await service.WriteAsync(document);

        Assert.True(result.Success);
        var sudo = runner.CommandLines("sudo");
        Assert.Equal(2, sudo.Count);
        Assert.StartsWith("-n /usr/bin/cp -p -- " + hosts + " " + hosts + ".pickle-backup", sudo[0], StringComparison.Ordinal);
        Assert.StartsWith("-n /usr/bin/install -m 0644 -o root -- ", sudo[1], StringComparison.Ordinal);
        Assert.EndsWith(" " + hosts, sudo[1], StringComparison.Ordinal);
        var staged = Directory.GetFiles(Path.Combine(_dir, "stage")).Single();
        Assert.Equal("127.0.0.1 localhost\n10.0.0.9        nas\n", File.ReadAllText(staged));
        Assert.Contains(staged, sudo[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task APasswordPromptReturnsTheCommandsForTheShell()
    {
        var (service, _, hosts) = Hosts(password: true);
        var document = await service.ReadAsync();
        document.Add(new HostsEntry("10.0.0.9", ["nas"], null, true));

        var result = await service.WriteAsync(document);

        Assert.False(result.Success);
        // PowerShellQuote leaves plain paths bare and single-quotes anything else (a Windows temp path has backslashes).
        Assert.StartsWith("sudo cp -p -- " + PowerShellQuote.Word(hosts) + " " + PowerShellQuote.Word(hosts + ".pickle-backup") + "; sudo install -m 0644 -o root -- ", result.ShellCommand, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInvalidFileIsRefusedBeforeAnythingIsStagedOrElevated()
    {
        var (service, runner, _) = Hosts();
        var document = HostsDocument.Parse("127.0.0.1 localhost\n");
        document.Add(new HostsEntry("10.0.0.9", ["ok"], null, true));
        var text = document.Serialize() + "garbage line\n";

        await Assert.ThrowsAsync<ArgumentException>(() => service.WriteAsync(HostsDocument.Parse(text)));

        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void EtcEnvironmentLinesAreReplacedRemovedAndAddedWithoutTouchingOthers()
    {
        const string original = "# system\nPATH=\"/usr/bin:/bin\"\nLANG=en_US.UTF-8\nEDITOR=nano\n";

        Assert.Equal("# system\nPATH=\"/usr/bin:/bin\"\nLANG=en_US.UTF-8\nEDITOR=\"vim\"\n", UnixEnvironmentStore.Apply(original, "EDITOR", "vim"));
        Assert.Equal("# system\nPATH=\"/usr/bin:/bin\"\nLANG=en_US.UTF-8\n", UnixEnvironmentStore.Apply(original, "EDITOR", null));
        Assert.EndsWith("NEW=\"a \\\"b\\\"\"\n", UnixEnvironmentStore.Apply(original, "NEW", "a \"b\""), StringComparison.Ordinal);
        Assert.Equal([("PATH", "/usr/bin:/bin"), ("LANG", "en_US.UTF-8"), ("EDITOR", "nano")], UnixEnvironmentStore.Parse(original));
    }

    [Fact]
    public async Task PickleScopeNeedsNoPrivilegesAndMachineScopeInstallsViaOneCommand()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The machine-wide scope (/etc/environment) exists on Linux only");
        using var t = TestPickle.Create();
        var file = Path.Combine(_dir, "environment");
        File.WriteAllText(file, "LANG=C\n");
        var runner = new FakeProgramRunner().On("install", "-m", string.Empty).On("sudo", "-n", string.Empty);
        var store = new UnixEnvironmentStore(t.Runtime.Config, new UnixPrivilegeService(runner, () => false, _ => null), file, Path.Combine(_dir, "stage"));

        var own = await store.SetAsync(EnvironmentScope.Pickle, "EDITOR", "vim");
        var path = await store.SetPathAsync(EnvironmentScope.Pickle, ["/opt/bin", "/usr/local/bin"]);
        var machine = await store.SetAsync(EnvironmentScope.Machine, "HTTP_PROXY", "http://proxy:3128");

        Assert.True(own.Success && path.Success && machine.Success);
        Assert.Equal("vim", t.Runtime.Config.Current.Shell.Environment["EDITOR"]);
        Assert.Equal(["/opt/bin", "/usr/local/bin"], t.Runtime.Config.Current.Shell.PathPrepend);
        Assert.Equal(["/opt/bin", "/usr/local/bin"], await store.GetPathAsync(EnvironmentScope.Pickle));
        Assert.Single(runner.CommandLines("sudo"));
        Assert.Contains("HTTP_PROXY=\"http://proxy:3128\"", File.ReadAllText(Directory.GetFiles(Path.Combine(_dir, "stage")).Single()), StringComparison.Ordinal);
        Assert.True(store.NeedsPrivileges(EnvironmentScope.Machine));
        Assert.False(store.NeedsPrivileges(EnvironmentScope.Pickle));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync(EnvironmentScope.Machine, "LD_PRELOAD", "/tmp/x.so"));
    }
}
