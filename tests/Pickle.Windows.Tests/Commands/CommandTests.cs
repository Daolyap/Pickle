using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Testing;
using Pickle.Testing.Fakes;

namespace Pickle.Windows.Tests.Commands;

public sealed class CommandTests : IDisposable
{
    private readonly TestPickle _t;
    private readonly FakeWingetService _winget = new();
    private readonly FakeWindowsUpdateService _wu = new();
    private readonly FakeTaskSchedulerService _tasks = new();
    private readonly FakeElevationBroker _broker = new();
    private readonly List<object?> _objects = [];
    private readonly List<string> _host = [];
    private readonly List<string> _errors = [];
    private readonly List<string> _questions = [];
    private bool? _answer;

    public CommandTests()
    {
        _t = TestPickle.Create(start: true, plugins: [new WindowsPlugin()]);
        var services = _t.Runtime.ServiceRegistry;
        services.Add<IWingetService>(_winget);
        services.Add<IWindowsUpdateService>(_wu);
        services.Add<ITaskSchedulerService>(_tasks);
        services.Add<IElevationBroker>(_broker);

        _winget.Installed.Add(new WingetPackage("Git.Git", "Git", "2.45.1", "2.46.0", "winget"));
        _winget.Installed.Add(new WingetPackage("Microsoft.PowerShell", "PowerShell 7", "7.4.5.0", null, "winget"));
        _winget.Catalog.Add(new WingetPackage("7zip.7zip", "7-Zip", null, "24.08", "winget"));
        _wu.Available.Add(new WindowsUpdateInfo("0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9", "Cumulative Update (KB5031455)", "KB5031455", ["Security Updates"], 1_000_000, false, false, false, false, "Critical", null, null, true));
        _wu.Available.Add(new WindowsUpdateInfo("11111111-2222-3333-4444-555555555555", "Intel Display Driver", null, ["Drivers"], 2_000_000, false, false, true, false, null, null, null, false));
    }

    public void Dispose() => _t.Dispose();

    private string Host => TextWidth.StripAnsi(string.Join('\n', _host));

    private async Task<int> RunAsync(string command, params string[] args)
    {
        var context = new PickleCommandContext
        {
            Pickle = _t.Runtime,
            WriteObject = _objects.Add,
            WriteHost = _host.Add,
            WriteError = _errors.Add,
            Confirm = (question, defaultYes) =>
            {
                _questions.Add(question);
                return _answer ?? defaultYes;
            },
            Interactive = true,
            Cwd = Path.GetTempPath(),
        };
        return await _t.Runtime.CommandRegistry.Get(command)!.ExecuteAsync(context, args, CancellationToken.None);
    }

    [Fact]
    public void PluginRegistersCommandsAndServices()
    {
        using var t = TestPickle.Create(start: true, plugins: [new WindowsPlugin()]);
        foreach (var name in new[] { "winget", "upgrade", "update", "schedule" })
        {
            Assert.NotNull(t.Runtime.CommandRegistry.Get(name));
        }

        Assert.NotNull(t.Runtime.Services.Get<IWingetService>());
        Assert.NotNull(t.Runtime.Services.Get<IElevationBroker>());
        var wu = t.Runtime.Services.Require<IWindowsUpdateService>();
        var tasks = t.Runtime.Services.Require<ITaskSchedulerService>();
        Assert.Equal(OperatingSystem.IsWindows(), wu.IsSupported);
        Assert.Equal(OperatingSystem.IsWindows(), tasks.IsSupported);
        Assert.Equal(TaskTriggerKind.AtLogon, tasks.ParseSchedule("at logon").Kind);
    }

    [Fact]
    public void PluginKeepsServicesRegisteredBeforeIt()
    {
        using var t = TestPickle.Create(start: false);
        var fake = new FakeWingetService();
        t.Runtime.ServiceRegistry.Add<IWingetService>(fake);
        t.Runtime.InitializeComponents();
        t.Runtime.Start([new WindowsPlugin()]);
        Assert.Same(fake, t.Runtime.Services.Get<IWingetService>());
    }

    [Fact]
    public void PkWingetListReturnsObjectsThroughThePipeline()
    {
        Assert.Equal(["Git.Git", "Microsoft.PowerShell"], _t.Run("(pk winget list).Id"));
        Assert.Equal(["Git.Git"], _t.Run("(pk winget upgrades).Id"));
    }

    [Fact]
    public async Task WingetListSearchAndShow()
    {
        Assert.Equal(0, await RunAsync("winget", "list"));
        Assert.Equal(2, _objects.Count);
        Assert.Contains("2 package(s) installed, 1 upgradable", Host, StringComparison.Ordinal);

        _objects.Clear();
        Assert.Equal(0, await RunAsync("winget", "search", "7zip"));
        Assert.Equal("7zip.7zip", Assert.IsType<WingetPackage>(Assert.Single(_objects)).Id);

        Assert.Equal(0, await RunAsync("winget", "show", "7zip.7zip"));
        Assert.IsType<WingetPackageDetails>(_objects[^1]);
        Assert.Contains("7-Zip [7zip.7zip]", Host, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WingetInstallConfirmsAndPassesOptions()
    {
        Assert.Equal(0, await RunAsync("winget", "install", "7zip.7zip", "--version", "24.08", "--scope", "machine"));
        Assert.Equal(["install 7zip.7zip"], _winget.Calls);
        Assert.Contains(_questions, q => q.Contains("machine-wide", StringComparison.Ordinal));

        _answer = false;
        Assert.Equal(1, await RunAsync("winget", "install", "7zip.7zip"));
        Assert.Single(_winget.Calls);
    }

    [Fact]
    public async Task WingetRejectsBadInput()
    {
        Assert.Equal(2, await RunAsync("winget", "install", "7zip;calc"));
        Assert.Equal(2, await RunAsync("winget", "install", "7zip.7zip", "--scope", "galaxy"));
        Assert.Equal(2, await RunAsync("winget", "install", "7zip.7zip", "--version"));
        Assert.Equal(2, await RunAsync("winget", "frobnicate"));
        Assert.Empty(_winget.Calls);
        Assert.NotEmpty(_errors);
    }

    [Fact]
    public async Task UninstallDefaultsToNoUnlessConfirmed()
    {
        _answer = null;
        Assert.Equal(1, await RunAsync("winget", "uninstall", "Git.Git"));
        Assert.Empty(_winget.Calls);

        Assert.Equal(0, await RunAsync("winget", "uninstall", "Git.Git", "--yes"));
        Assert.Equal(["uninstall Git.Git"], _winget.Calls);
    }

    [Fact]
    public async Task UpgradeAllAndElevatedUpgrade()
    {
        Assert.Equal(0, await RunAsync("winget", "upgrade", "--all"));
        Assert.Equal(["upgrade Git.Git"], _winget.Calls);

        Assert.Equal(0, await RunAsync("winget", "upgrade", "--all", "--elevated"));
        var request = Assert.Single(_broker.Requests);
        Assert.Equal(ElevatedOperationKind.WingetUpgrade, request.Kind);
        Assert.Equal(["Git.Git"], request.Arguments);

        _broker.DeclineUac = true;
        Assert.Equal(1, await RunAsync("winget", "upgrade", "Git.Git", "--elevated"));
        Assert.Contains("declined", Host, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepairSourceAndModuleInstallAskFirst()
    {
        Assert.Equal(0, await RunAsync("winget", "repair-source", "--admin"));
        Assert.Equal(["repair-source elevated"], _winget.Calls);
        Assert.Contains(_questions, q => q.Contains("UAC", StringComparison.Ordinal));

        Assert.Equal(0, await RunAsync("winget", "install-module"));
        Assert.Contains("install-module", _winget.Calls);
    }

    [Fact]
    public async Task PkUpgradeIncludesWindowsUpdatesWithOneConfirmation()
    {
        Assert.Equal(0, await RunAsync("upgrade"));
        Assert.Single(_questions);
        Assert.Contains("1 package upgrade(s) and 1 Windows update(s)", _questions[0], StringComparison.Ordinal);
        Assert.Contains("UAC", _questions[0], StringComparison.Ordinal);
        Assert.Equal(["upgrade Git.Git"], _winget.Calls);
        Assert.Equal(["0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9"], _wu.Installed);
    }

    [Fact]
    public async Task PkUpgradeHonorsConfigAndCancellation()
    {
        _t.Runtime.Config.Update(c => c.Winget.IncludeWindowsUpdatesInUpgrade = false);
        _answer = false;
        Assert.Equal(1, await RunAsync("upgrade"));
        Assert.Contains("1 package upgrade(s) and 0 Windows update(s)?", _questions[0], StringComparison.Ordinal);
        Assert.Empty(_winget.Calls);
        Assert.Empty(_wu.Installed);
    }

    [Fact]
    public async Task UpdateCheckInstallHistoryStatus()
    {
        Assert.Equal(0, await RunAsync("update", "check", "--drivers"));
        Assert.Equal(2, _objects.OfType<WindowsUpdateInfo>().Count());
        Assert.Contains("Security & critical", Host, StringComparison.Ordinal);

        Assert.Equal(2, await RunAsync("update", "install"));
        Assert.Equal(2, await RunAsync("update", "install", "--kb", "nope"));

        Assert.Equal(0, await RunAsync("update", "install", "--kb", "kb5031455"));
        Assert.Equal(["0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9"], _wu.Installed);

        Assert.Equal(0, await RunAsync("update", "install", "--id", "{11111111-2222-3333-4444-555555555555}"));
        Assert.Equal("11111111-2222-3333-4444-555555555555", _wu.Installed[^1]);

        Assert.Equal(0, await RunAsync("update", "status"));
        Assert.IsType<WindowsUpdateStatus>(_objects[^1]);
        Assert.Contains("managed by organization: no", Host, StringComparison.Ordinal);

        _wu.History.Add(new WindowsUpdateHistoryEntry("KB1", DateTimeOffset.Now, "Installation", "Succeeded", "KB1234567"));
        Assert.Equal(0, await RunAsync("update", "history", "--max", "5"));
        Assert.IsType<WindowsUpdateHistoryEntry>(_objects[^1]);
    }

    [Fact]
    public async Task ScheduleAddCreatesAPickleTask()
    {
        Assert.Equal(0, await RunAsync("schedule", "add", "every 30m", "pk upgrade --yes", "--name", "nightly"));
        var info = Assert.IsType<ScheduledTaskInfo>(_objects[^1]);
        Assert.Equal(@"\Pickle\nightly", info.Path);
        Assert.Equal([@"create \Pickle\nightly"], _tasks.Calls);
        Assert.Contains("-NoLogo -c \"pk upgrade --yes\"", info.Actions[0], StringComparison.Ordinal);
        Assert.Empty(_questions);
    }

    [Fact]
    public async Task ScheduleAddElevatedAsksAboutUac()
    {
        Assert.Equal(0, await RunAsync("schedule", "add", "at logon", "Get-Date", "--elevated", "--name", "admin-task"));
        Assert.Contains(_questions, q => q.Contains("UAC", StringComparison.Ordinal));
        Assert.True(_tasks.Tasks.Single().RunElevated);
    }

    [Fact]
    public async Task ScheduleManagementCommands()
    {
        await RunAsync("schedule", "add", "daily 09:00", "Get-Date", "--name", "t1");
        Assert.Equal(0, await RunAsync("schedule", "list"));
        Assert.Equal(@"\Pickle\t1", _objects.OfType<ScheduledTaskInfo>().Last().Path);

        Assert.Equal(0, await RunAsync("schedule", "run", "t1"));
        Assert.Equal(0, await RunAsync("schedule", "disable", "t1"));
        Assert.Equal(0, await RunAsync("schedule", "enable", @"\Pickle\t1"));
        Assert.Equal(1, await RunAsync("schedule", "remove", "t1"));
        Assert.Equal(0, await RunAsync("schedule", "remove", "t1", "-y"));
        Assert.Equal(0, await RunAsync("schedule", "history", "t1"));
        Assert.Equal([@"create \Pickle\t1", @"run \Pickle\t1", @"disable \Pickle\t1", @"enable \Pickle\t1", @"delete \Pickle\t1"], _tasks.Calls);
    }

    [Fact]
    public async Task ScheduleRejectsBadInput()
    {
        _tasks.Calls.Clear();
        var windowsParser = new FakeTaskSchedulerServiceWithRealParser(_tasks);
        _t.Runtime.ServiceRegistry.Add<ITaskSchedulerService>(windowsParser);
        Assert.Equal(2, await RunAsync("schedule", "add", "sometimes", "Get-Date"));
        Assert.Contains("Unrecognized schedule", _errors[^1], StringComparison.Ordinal);
        Assert.Equal(2, await RunAsync("schedule", "add", "daily 09:00"));
        Assert.Equal(2, await RunAsync("schedule", "add", "daily 09:00", "Get-Date", "--name", "bad|name"));
        Assert.Equal(2, await RunAsync("schedule", "run", "bad|name"));
        Assert.Empty(_tasks.Calls);
    }

    [Fact]
    public async Task UnsupportedServicesReportClearly()
    {
        _t.Runtime.ServiceRegistry.Add<IWingetService>(new FakeWingetService { IsSupported = false });
        Assert.Equal(1, await RunAsync("winget", "list"));
        Assert.Contains("only available on Windows", _errors[^1], StringComparison.Ordinal);
    }

    private sealed class FakeTaskSchedulerServiceWithRealParser(FakeTaskSchedulerService inner) : ITaskSchedulerService
    {
        public bool IsSupported => true;

        public Task<IReadOnlyList<string>> GetFoldersAsync(string root = @"\", CancellationToken cancellationToken = default) => inner.GetFoldersAsync(root, cancellationToken);

        public Task<IReadOnlyList<ScheduledTaskInfo>> GetTasksAsync(string folder = @"\", bool recurse = false, CancellationToken cancellationToken = default) => inner.GetTasksAsync(folder, recurse, cancellationToken);

        public Task<ScheduledTaskInfo?> GetTaskAsync(string path, CancellationToken cancellationToken = default) => inner.GetTaskAsync(path, cancellationToken);

        public Task RunAsync(string path, CancellationToken cancellationToken = default) => inner.RunAsync(path, cancellationToken);

        public Task StopAsync(string path, CancellationToken cancellationToken = default) => inner.StopAsync(path, cancellationToken);

        public Task SetEnabledAsync(string path, bool enabled, CancellationToken cancellationToken = default) => inner.SetEnabledAsync(path, enabled, cancellationToken);

        public Task DeleteAsync(string path, CancellationToken cancellationToken = default) => inner.DeleteAsync(path, cancellationToken);

        public Task<IReadOnlyList<ScheduledTaskRun>> GetHistoryAsync(string path, int max = 50, CancellationToken cancellationToken = default) => inner.GetHistoryAsync(path, max, cancellationToken);

        public Task<ScheduledTaskInfo> CreateAsync(ScheduledTaskDefinition definition, CancellationToken cancellationToken = default) => inner.CreateAsync(definition, cancellationToken);

        public TaskTriggerSpec ParseSchedule(string text) => Pickle.Windows.TaskScheduler.ScheduleParser.Parse(text);
    }
}
