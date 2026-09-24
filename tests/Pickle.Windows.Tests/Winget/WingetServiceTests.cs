using Pickle.Abstractions.Services;
using Pickle.Testing.Fakes;
using Pickle.Windows.Processes;
using Pickle.Windows.Winget;

namespace Pickle.Windows.Tests.Winget;

public class WingetServiceTests
{
    private const string Exe = @"C:\Users\me\AppData\Local\Microsoft\WindowsApps\winget.exe";

    private static (WingetService Service, FakeProcessRunner Runner, FakeElevationBroker Broker) Create(bool withExe = true)
    {
        var runner = new FakeProcessRunner();
        var broker = new FakeElevationBroker();
        var service = new WingetService(new NullShell(), () => broker, new ListLogger(), runner, () => withExe ? Exe : null, isSupported: true);
        return (service, runner, broker);
    }

    [Fact]
    public async Task FallsBackToTheCliAndParsesOutput()
    {
        var (service, runner, _) = Create();
        runner.Replies["list"] = new ProcessResult(0, WingetFixtures.List);

        Assert.Equal(WingetBackend.Cli, await service.GetBackendAsync());
        var installed = await service.ListInstalledAsync();

        Assert.Equal(6, installed.Count);
        var call = Assert.Single(runner.Calls);
        Assert.Equal(Exe, call.File);
        Assert.Equal(["list", "--accept-source-agreements", "--disable-interactivity"], call.Args);
    }

    [Fact]
    public async Task UpgradesAndSearchUseTheRightArguments()
    {
        var (service, runner, _) = Create();
        runner.Replies["upgrade"] = new ProcessResult(0, WingetFixtures.Upgrade);
        runner.Replies["search"] = new ProcessResult(0, WingetFixtures.Search);

        Assert.Equal(3, (await service.ListUpgradesAsync(includeUnknown: true)).Count);
        Assert.Equal(["upgrade", "--include-unknown", "--accept-source-agreements", "--disable-interactivity"], runner.Calls[0].Args);

        Assert.Equal(3, (await service.SearchAsync("  wechat & calc ")).Count);
        Assert.Equal(["search", "--query", "wechat & calc", "--accept-source-agreements", "--disable-interactivity"], runner.Calls[1].Args);
    }

    [Fact]
    public async Task InstallBuildsASafeArgumentListAndReportsProgress()
    {
        var (service, runner, _) = Create();
        runner.Replies["install"] = new ProcessResult(0, "Found Git [Git.Git] Version 2.46.0\r\nDownloading https://x\r\n  ████  10.0 MB / 20.0 MB\r\nStarting package install...\r\nSuccessfully installed\r\n");
        var progress = new List<WingetProgress>();

        var result = await service.InstallAsync("Git.Git", new WingetInstallOptions("2.46.0", WingetScope.User, Force: true), new SyncProgress<WingetProgress>(progress.Add));

        Assert.True(result.Success);
        Assert.Equal(
            ["install", "--id", "Git.Git", "--exact", "--silent", "--accept-package-agreements", "--version", "2.46.0", "--scope", "user", "--force", "--accept-source-agreements", "--disable-interactivity"],
            runner.Calls.Single().Args);
        Assert.Contains(progress, p => p.Stage == "Downloading" && p.Percent == 50);
        Assert.Equal("Done", progress[^1].Stage);
    }

    [Fact]
    public async Task MachineScopeInstallsGoThroughTheBroker()
    {
        var (service, runner, broker) = Create();
        var result = await service.InstallAsync("7zip.7zip", new WingetInstallOptions(Scope: WingetScope.Machine));

        Assert.True(result.Success);
        Assert.Empty(runner.Calls);
        var request = Assert.Single(broker.Requests);
        Assert.Equal(ElevatedOperationKind.WingetInstall, request.Kind);
        Assert.Equal(["7zip.7zip"], request.Arguments);

        broker.DeclineUac = true;
        var declined = await service.InstallAsync("7zip.7zip", new WingetInstallOptions(Scope: WingetScope.Machine));
        Assert.False(declined.Success);
        Assert.Contains("declined", declined.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ElevatedRepairUsesTheBroker()
    {
        var (service, _, broker) = Create();
        Assert.True((await service.RepairSourceAsync(elevated: true)).Success);
        Assert.Equal(ElevatedOperationKind.WingetRepairSource, Assert.Single(broker.Requests).Kind);
    }

    [Theory]
    [InlineData("Git.Git; calc")]
    [InlineData("--source evil")]
    [InlineData("Contoso.Truncated…")]
    [InlineData("")]
    public async Task InvalidIdsNeverReachWinget(string id)
    {
        var (service, runner, broker) = Create();
        await Assert.ThrowsAsync<ArgumentException>(() => service.InstallAsync(id, new WingetInstallOptions()));
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpgradeAsync(id, new WingetInstallOptions()));
        await Assert.ThrowsAsync<ArgumentException>(() => service.UninstallAsync(id));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetDetailsAsync(id));
        Assert.Empty(runner.Calls);
        Assert.Empty(broker.Requests);
    }

    [Fact]
    public async Task VersionsAreValidated()
    {
        var (service, runner, _) = Create();
        await Assert.ThrowsAsync<ArgumentException>(() => service.InstallAsync("Git.Git", new WingetInstallOptions("1.0 --override x")));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task FailuresAreFriendly()
    {
        var (service, runner, _) = Create();
        runner.Replies["uninstall"] = new ProcessResult(unchecked((int)0x8A150014), "No installed package found matching input criteria.\r\n");
        var result = await service.UninstallAsync("Nope.Nope");
        Assert.False(result.Success);
        Assert.Contains("No package found", result.Message, StringComparison.Ordinal);
        Assert.Equal(["uninstall", "--id", "Nope.Nope", "--exact", "--silent", "--accept-source-agreements", "--disable-interactivity"], runner.Calls.Single().Args);
    }

    [Fact]
    public async Task UnavailableAndUnsupportedBackends()
    {
        var (service, _, _) = Create(withExe: false);
        Assert.Equal(WingetBackend.Unavailable, await service.GetBackendAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ListInstalledAsync());

        var unsupported = new WingetService(new NullShell(), () => null, new ListLogger(), new FakeProcessRunner(), () => Exe, isSupported: false);
        Assert.False(unsupported.IsSupported);
        Assert.Empty(await unsupported.ListInstalledAsync());
        Assert.False((await unsupported.InstallAsync("Git.Git", new WingetInstallOptions())).Success);
    }
}
