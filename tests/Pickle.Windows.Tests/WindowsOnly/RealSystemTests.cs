using Pickle.Abstractions.Services;
using Pickle.Testing.Fakes;
using Pickle.Windows.TaskScheduler;
using Pickle.Windows.WindowsUpdate;

namespace Pickle.Windows.Tests.WindowsOnly;

/// <summary>Touch the real system (read-only WUA search, a throwaway task under \Pickle\Test, winget). Windows CI only.</summary>
public class RealSystemTests
{
    [Fact]
    public async Task WindowsUpdateStatusAndSearchAreReadable()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
        }

        var service = new WindowsUpdateService(new ListLogger(), () => new FakeElevationBroker(), new RegistryPolicySource());
        var status = await service.GetStatusAsync();
        Assert.True(status.IsSupported);

        try
        {
            var updates = await service.SearchAsync(new WindowsUpdateQuery(IncludeDrivers: false));
            Assert.All(updates, u => Assert.False(u.IsDriver));
        }
        catch (InvalidOperationException ex)
        {
            // CI machines may be offline or managed; the error must still be a friendly message.
            Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        }

        Assert.NotNull(await service.GetHistoryAsync(5));
    }

    [Fact]
    public async Task CreatesRunsAndDeletesATask()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
        }

        var service = new TaskSchedulerService(new ListLogger(), () => null);
        var name = "pickle-test-" + Guid.NewGuid().ToString("N")[..8];
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var definition = new ScheduledTaskDefinition(
            name,
            service.ParseSchedule("daily 03:00"),
            new TaskActionSpec(cmd, "/c exit 0"),
            @"\Pickle\Test",
            "Pickle test task");
        try
        {
            var info = await service.CreateAsync(definition);
            Assert.Equal($@"\Pickle\Test\{name}", info.Path);
            Assert.Contains(await service.GetTasksAsync(@"\Pickle\Test"), t => t.Name == name);
            Assert.Contains(@"\Pickle\Test", await service.GetFoldersAsync(@"\Pickle"));
            await service.SetEnabledAsync(info.Path, false);
            Assert.False((await service.GetTaskAsync(info.Path))!.Enabled);
            _ = await service.GetHistoryAsync(info.Path, 5);
        }
        finally
        {
            await service.DeleteAsync($@"\Pickle\Test\{name}");
        }

        Assert.Null(await service.GetTaskAsync($@"\Pickle\Test\{name}"));
    }

    [Fact]
    public async Task WingetIsDetected()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
        }

        if (Pickle.Windows.Winget.WingetService.LocateWingetExe() is null)
        {
            Assert.Skip("winget is not installed on this machine");
        }

        var service = new Pickle.Windows.Winget.WingetService(new NullShell(), () => null, new ListLogger(), new Pickle.Windows.Processes.ProcessRunner(new ListLogger()), Pickle.Windows.Winget.WingetService.LocateWingetExe, isSupported: true);
        Assert.Equal(WingetBackend.Cli, await service.GetBackendAsync());
        Assert.NotEmpty(await service.ListSourcesAsync());
    }
}
