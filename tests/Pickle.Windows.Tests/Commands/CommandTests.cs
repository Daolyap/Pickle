using System.Diagnostics;
using System.Management.Automation;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Testing;
using Pickle.Testing.Fakes;
using Pickle.Windows.Commands;

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

    private string Host
    {
        get
        {
            lock (_host)
            {
                return TextWidth.StripAnsi(string.Join('\n', _host));
            }
        }
    }

    private IEnumerable<T> Objects<T>() => _objects.Select(Display.Unwrap).OfType<T>();

    private Task<int> RunAsync(string command, params string[] args) => RunAsync(command, CancellationToken.None, args);

    private async Task<int> RunAsync(string command, CancellationToken cancellationToken, params string[] args)
    {
        var context = new PickleCommandContext
        {
            Pickle = _t.Runtime,
            WriteObject = _objects.Add,
            WriteHost = line =>
            {
                lock (_host)
                {
                    _host.Add(line);
                }
            },
            WriteError = _errors.Add,
            Confirm = (question, defaultYes) =>
            {
                _questions.Add(question);
                return _answer ?? defaultYes;
            },
            Interactive = true,
            Cwd = Path.GetTempPath(),
        };
        return await _t.Runtime.CommandRegistry.Get(command)!.ExecuteAsync(context, args, cancellationToken);
    }

    [Fact]
    public void PluginRegistersCommandsAndServices()
    {
        using var t = TestPickle.Create(start: true, plugins: [new WindowsPlugin()]);
        foreach (var name in new[] { "winget", "tool", "upgrade", "update", "schedule" })
        {
            Assert.NotNull(t.Runtime.CommandRegistry.Get(name));
        }

        Assert.NotNull(t.Runtime.Services.Get<IWingetService>());
        Assert.NotNull(t.Runtime.Services.Get<IElevationBroker>());
        var wu = t.Runtime.Services.Require<IWindowsUpdateService>();
        var tasks = t.Runtime.Services.Require<ITaskSchedulerService>();
        Assert.Equal(OperatingSystem.IsWindows(), t.Runtime.Services.Require<IToolInstaller>().IsSupported);
        Assert.Equal(OperatingSystem.IsWindows(), wu.IsSupported);
        Assert.Equal(OperatingSystem.IsWindows(), tasks.IsSupported);
        Assert.Equal(TaskTriggerKind.AtLogon, tasks.ParseSchedule("at logon").Kind);
    }

    [Fact]
    public async Task ToolInstallPassesScopeAndPathChoices()
    {
        var installer = new FakeToolInstaller();
        _t.Runtime.ServiceRegistry.Add<IToolInstaller>(installer);

        Assert.Equal(0, await RunAsync("tool", "install", "7z", "--temp", "--no-path"));
        Assert.Equal(0, await RunAsync("tool", "install", "nmap", "--machine"));
        Assert.Equal(0, await RunAsync("tool", "install", "Some.Tool"));

        Assert.Equal(
            [
                ("7zip.7zip", new ToolInstallOptions(ToolInstallScope.Temporary, false)),
                ("Insecure.Nmap", new ToolInstallOptions(ToolInstallScope.Machine, true)),
                ("Some.Tool", new ToolInstallOptions(ToolInstallScope.User, true)),
            ],
            installer.Installs.Select(i => (i.Package.WingetId, i.Options)));
        Assert.Contains("Installed 7-Zip.", Host, StringComparison.Ordinal);

        Assert.Equal(1, await RunAsync("tool", "install", "frobnicate"));
        Assert.Contains(_errors, e => e.Contains("No known package provides 'frobnicate'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ToolListShowsTheCatalog()
    {
        Assert.Equal(0, await RunAsync("tool", "list", "zip"));
        var row = Assert.Single(Objects<ToolCommand.ToolRow>());
        Assert.Equal(("7z", "7zip.7zip"), (row.Command, row.WingetId));
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
        Assert.Equal("7zip.7zip", Assert.IsType<WingetPackage>(Display.Unwrap(Assert.Single(_objects))).Id);

        Assert.Equal(0, await RunAsync("winget", "show", "7zip.7zip"));
        Assert.IsType<WingetPackageDetails>(Display.Unwrap(_objects[^1]));
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
    public async Task RepairSourceDefaultsToTheCurrentUser()
    {
        Assert.Equal(0, await RunAsync("winget", "repair-source", "--yes"));
        Assert.Equal(["repair-source"], _winget.Calls);
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
        Assert.Equal(2, Objects<WindowsUpdateInfo>().Count());
        Assert.Equal("Security & critical", ((PSObject)_objects[0]!).Properties["Kind"].Value);
        Assert.Contains("2 update(s) available", Host, StringComparison.Ordinal);

        Assert.Equal(2, await RunAsync("update", "install"));
        Assert.Equal(2, await RunAsync("update", "install", "--kb", "nope"));

        Assert.Equal(0, await RunAsync("update", "install", "--kb", "kb5031455"));
        Assert.Equal(["0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9"], _wu.Installed);

        Assert.Equal(0, await RunAsync("update", "install", "--id", "{11111111-2222-3333-4444-555555555555}"));
        Assert.Equal("11111111-2222-3333-4444-555555555555", _wu.Installed[^1]);

        Assert.Contains("Security & critical", Host, StringComparison.Ordinal);

        Assert.Equal(0, await RunAsync("update", "status"));
        Assert.IsType<WindowsUpdateStatus>(_objects[^1]);
        Assert.Contains("managed by organization: no", Host, StringComparison.Ordinal);

        _wu.History.Add(new WindowsUpdateHistoryEntry("KB1", DateTimeOffset.Now, "Installation", "Succeeded", "KB1234567"));
        Assert.Equal(0, await RunAsync("update", "history", "--max", "5"));
        Assert.IsType<WindowsUpdateHistoryEntry>(Display.Unwrap(_objects[^1]));
    }

    [Fact]
    public async Task UpdateCheckShowsLiveProgressFromTheSearchThread()
    {
        _wu.SearchDelay = TimeSpan.FromSeconds(1.2);
        Assert.Equal(0, await RunAsync("update", "check").WaitAsync(TimeSpan.FromSeconds(20)));

        Assert.True(_wu.ProgressReports > 0);
        Assert.Contains("Searching Windows Update…", Host, StringComparison.Ordinal);
        Assert.Contains("Searching Windows Update — done in 0:01", Host, StringComparison.Ordinal);
        Assert.Contains("1 update(s) available", Host, StringComparison.Ordinal);
        // Each spinner frame rewrites the previous one in place, and the next line replaces the last frame.
        lock (_host)
        {
            Assert.All(_host.Skip(1).Where(l => l.Contains("Searching Windows Update", StringComparison.Ordinal)), l => Assert.StartsWith(Ansi.CursorUp(1), l, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task UpdateCheckStopsWaitingOnCtrlCEvenIfTheSearchCannotBeAborted()
    {
        _wu.SearchDelay = TimeSpan.FromMinutes(2);
        _wu.IgnoreCancellation = true;
        using var ctrlC = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync("update", ctrlC.Token, "check").WaitAsync(TimeSpan.FromSeconds(20)));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
        Assert.Contains("Searching Windows Update — cancelled after", Host, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateCheckTimesOut()
    {
        _wu.SearchDelay = TimeSpan.FromMinutes(2);
        _wu.IgnoreCancellation = true;
        Assert.Equal(1, await RunAsync("update", "check", "--timeout", "1s").WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Contains("did not finish within 0:01", _errors[^1], StringComparison.Ordinal);
        Assert.Equal(2, await RunAsync("update", "check", "--timeout", "soon"));
    }

    [Fact]
    public async Task PkUpdateCheckThroughThePipelineDoesNotDeadlock()
    {
        _wu.SearchDelay = TimeSpan.FromSeconds(1);
        var ids = await Task.Run(() => _t.Run("(pk update check --drivers).UpdateId")).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(["0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9", "11111111-2222-3333-4444-555555555555"], ids);
        Assert.True(_wu.ProgressReports > 0);
        Assert.Contains("done in", _t.Terminal.GetScreenText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateInstallByKbLooksEverywhereAndExplainsMissingKbs()
    {
        _wu.Available.Add(new WindowsUpdateInfo("22222222-2222-3333-4444-555555555555", "2026-09 Preview (KB5031999)", "KB5031999", ["Updates"], 1_000, false, false, false, true, null, null, null, true));
        _wu.History.Add(new WindowsUpdateHistoryEntry("2026-08 Cumulative Update (KB5030000)", new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero), "Installation", "Succeeded", "KB5030000"));

        Assert.Equal(0, await RunAsync("update", "install", "--kb", "5031999, KB5030000,KB5000001", "--yes"));

        Assert.Equal(["22222222-2222-3333-4444-555555555555"], _wu.Installed);
        Assert.True(_wu.Queries[^1].IncludeOptional);
        Assert.True(_wu.Queries[^1].IncludeDrivers);
        Assert.Contains("KB5030000 is already installed", Host, StringComparison.Ordinal);
        Assert.Contains("KB5000001 is not offered for this PC", Host, StringComparison.Ordinal);

        Assert.Equal(1, await RunAsync("update", "install", "--kb", "KB5000001", "--yes"));
        Assert.Contains("No matching updates", _errors[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task WingetUninstallsSeveralPackagesOrElevated()
    {
        Assert.Equal(0, await RunAsync("winget", "uninstall", "Git.Git", "7zip.7zip", "--yes"));
        Assert.Equal(["uninstall Git.Git", "uninstall 7zip.7zip"], _winget.Calls);

        Assert.Equal(0, await RunAsync("winget", "uninstall", "Git.Git", "7zip.7zip", "--elevated", "--yes"));
        Assert.Equal("uninstall-elevated Git.Git,7zip.7zip", _winget.Calls[^1]);

        Assert.Equal(2, await RunAsync("winget", "uninstall"));
        Assert.Equal(2, await RunAsync("winget", "uninstall", "Git.Git", "bad;id", "--yes"));
        Assert.Equal(3, _winget.Calls.Count);
    }

    [Fact]
    public async Task FailedOperationsShowTheirOutput()
    {
        _winget.Result = call => call == "repair-source"
            ? new WingetOperationResult(false, "Repairing the winget source failed: The source package is in use. (0x80073D02)", unchecked((int)0x80073D02))
            {
                Output = "Add-AppxPackage : Deployment failed with HRESULT: 0x80073D02\nerror 0x80073D02: close WindowsPackageManagerServer.exe",
            }
            : null;

        Assert.Equal(1, await RunAsync("winget", "repair-source", "--yes"));
        Assert.Contains("✗ Repairing the winget source failed: The source package is in use. (0x80073D02)", Host, StringComparison.Ordinal);
        Assert.Contains("  │ Add-AppxPackage : Deployment failed with HRESULT: 0x80073D02", Host, StringComparison.Ordinal);
        Assert.Contains("  │ error 0x80073D02: close WindowsPackageManagerServer.exe", Host, StringComparison.Ordinal);
        Assert.DoesNotContain(_objects, o => o is WingetOperationResult);
    }

    [Theory]
    [InlineData(80)]
    [InlineData(120)]
    public void WingetListIsATableThatFitsTheWidth(int width)
    {
        _winget.Installed.Add(new WingetPackage("Microsoft.VCRedist.2015+.x64", "Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.40.33810", "14.40.33810.0", "14.42.34433.0", "winget"));
        var text = string.Join('\n', _t.Run($"pk winget list | Out-String -Width {width}"));
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();

        Assert.Matches(@"^Id\s+Version\s+Available\s+Name$", lines[0].TrimEnd());
        Assert.Matches(@"^-+\s+-+\s+-+\s+-+$", lines[1].TrimEnd());
        Assert.Equal(5, lines.Count);
        Assert.All(lines, l => Assert.True(l.Length <= width, $"{l.Length} > {width}: {l}"));
        Assert.Contains(lines, l => l.StartsWith("Git.Git ", StringComparison.Ordinal) && l.Contains("2.46.0", StringComparison.Ordinal) && l.TrimEnd().EndsWith(" Git", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("Microsoft.VCRedist.2015+.x64 ", StringComparison.Ordinal) && l.Contains("14.42.34433.0", StringComparison.Ordinal));
        Assert.DoesNotContain("IsUpgradable", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(80)]
    [InlineData(120)]
    public void UpgradeListsAreAlignedAndFit(int width)
    {
        IReadOnlyList<WingetPackage> packages =
        [
            new("Git.Git", "Git", "2.45.1", "2.46.0", "winget"),
            new("Microsoft.VCRedist.2015+.x64", "Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.40.33810", "14.40.33810.0", "14.42.34433.0", "winget"),
            new("JetBrains.IntelliJIDEA.Community.EAP.WithAVeryLongId", "IntelliJ IDEA", "2026.2", "2026.3", "winget"),
        ];

        var lines = WingetCommand.PackageLines(packages, width);

        Assert.Equal(3, lines.Count);
        Assert.All(lines, l => Assert.True(TextWidth.VisibleWidth(l) < width, $"{TextWidth.VisibleWidth(l)} >= {width}: {l}"));
        Assert.Single(lines.Select(l => l.IndexOf('→', StringComparison.Ordinal)).Distinct());
        Assert.StartsWith("  Git ", lines[0], StringComparison.Ordinal);
        Assert.Contains("…", lines[1], StringComparison.Ordinal);
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
