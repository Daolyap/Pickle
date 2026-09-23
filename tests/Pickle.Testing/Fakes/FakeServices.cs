using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Testing.Fakes;

/// <summary>In-memory git service. Set <see cref="Status"/> etc.; mutating calls are recorded in <see cref="Calls"/>.</summary>
public sealed class FakeGitService : IGitService
{
    public GitStatus? Status { get; set; }
    public string Diff { get; set; } = string.Empty;
    public List<GitBranch> Branches { get; } = [];
    public List<GitCommit> Log { get; } = [];
    public List<GitStash> Stashes { get; } = [];
    public List<string> Calls { get; } = [];
    public bool IsGitAvailable { get; set; } = true;

    public Task<string?> FindRepositoryRootAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(Status?.Root);
    public Task<GitStatus?> GetStatusAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(Status);
    public Task<string> GetDiffAsync(string repo, string? file, bool staged, CancellationToken cancellationToken = default) => Task.FromResult(Diff);
    public Task<GitCommandResult> StageAsync(string repo, IReadOnlyList<string> paths, CancellationToken cancellationToken = default) => Record("stage " + string.Join(' ', paths));
    public Task<GitCommandResult> UnstageAsync(string repo, IReadOnlyList<string> paths, CancellationToken cancellationToken = default) => Record("unstage " + string.Join(' ', paths));
    public Task<GitCommandResult> DiscardAsync(string repo, IReadOnlyList<string> paths, CancellationToken cancellationToken = default) => Record("discard " + string.Join(' ', paths));
    public Task<GitCommandResult> ApplyPatchToIndexAsync(string repo, string patch, bool reverse, CancellationToken cancellationToken = default) => Record((reverse ? "unapply " : "apply ") + patch.Length);
    public Task<GitCommandResult> CommitAsync(string repo, string message, bool amend = false, CancellationToken cancellationToken = default) => Record((amend ? "amend " : "commit ") + message);
    public Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string repo, bool includeRemote = true, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GitBranch>>(Branches);
    public Task<GitCommandResult> CheckoutAsync(string repo, string branch, bool create = false, CancellationToken cancellationToken = default) => Record((create ? "checkout -b " : "checkout ") + branch);
    public Task<GitCommandResult> DeleteBranchAsync(string repo, string branch, bool force = false, CancellationToken cancellationToken = default) => Record("branch -d " + branch);
    public Task<IReadOnlyList<GitCommit>> GetLogAsync(string repo, int max = 200, bool graph = true, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GitCommit>>(Log);
    public Task<IReadOnlyList<GitStash>> GetStashesAsync(string repo, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GitStash>>(Stashes);
    public Task<GitCommandResult> StashPushAsync(string repo, string? message, bool includeUntracked, CancellationToken cancellationToken = default) => Record("stash push " + message);
    public Task<GitCommandResult> StashApplyAsync(string repo, int index, bool pop, CancellationToken cancellationToken = default) => Record((pop ? "stash pop " : "stash apply ") + index);
    public Task<GitCommandResult> StashDropAsync(string repo, int index, CancellationToken cancellationToken = default) => Record("stash drop " + index);
    public Task<GitCommandResult> FetchAsync(string repo, CancellationToken cancellationToken = default) => Record("fetch");
    public Task<GitCommandResult> PullAsync(string repo, CancellationToken cancellationToken = default) => Record("pull");
    public Task<GitCommandResult> PushAsync(string repo, bool setUpstream = false, CancellationToken cancellationToken = default) => Record("push");
    public Task<GitCommandResult> RunAsync(string repo, IReadOnlyList<string> args, CancellationToken cancellationToken = default) => Record(string.Join(' ', args));

    private Task<GitCommandResult> Record(string call)
    {
        Calls.Add(call);
        return Task.FromResult(new GitCommandResult(true, string.Empty, string.Empty, 0));
    }
}

public sealed class FakeWingetService : IWingetService
{
    public bool IsSupported { get; set; } = true;
    public WingetBackend Backend { get; set; } = WingetBackend.PowerShellModule;
    public List<WingetPackage> Installed { get; } = [];
    public List<WingetPackage> Catalog { get; } = [];
    public List<WingetSource> Sources { get; } = [new("winget", "https://cdn.winget.microsoft.com/cache", "Microsoft.PreIndexed.Package")];
    public List<string> Calls { get; } = [];

    public Task<WingetBackend> GetBackendAsync(CancellationToken cancellationToken = default) => Task.FromResult(Backend);
    public Task<WingetOperationResult> InstallClientModuleAsync(CancellationToken cancellationToken = default) => Ok("install-module");
    public Task<IReadOnlyList<WingetPackage>> ListInstalledAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WingetPackage>>(Installed);
    public Task<IReadOnlyList<WingetPackage>> ListUpgradesAsync(bool includeUnknown = false, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WingetPackage>>([.. Installed.Where(p => p.IsUpgradable)]);
    public Task<IReadOnlyList<WingetPackage>> SearchAsync(string query, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WingetPackage>>([.. Catalog.Where(p => p.Id.Contains(query, StringComparison.OrdinalIgnoreCase) || p.Name.Contains(query, StringComparison.OrdinalIgnoreCase))]);
    public Task<WingetPackageDetails?> GetDetailsAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult<WingetPackageDetails?>(Catalog.Concat(Installed).FirstOrDefault(p => p.Id == id) is { } p
            ? new WingetPackageDetails(p.Id, p.Name, p.Publisher, $"{p.Name} description", null, "MIT", p.AvailableVersion ?? p.InstalledVersion, [p.AvailableVersion ?? p.InstalledVersion ?? "1.0"])
            : null);
    public Task<WingetOperationResult> InstallAsync(string id, WingetInstallOptions options, IProgress<WingetProgress>? progress = null, CancellationToken cancellationToken = default) => Ok("install " + id, progress);
    public Task<WingetOperationResult> UpgradeAsync(string id, WingetInstallOptions options, IProgress<WingetProgress>? progress = null, CancellationToken cancellationToken = default) => Ok("upgrade " + id, progress);
    public Task<WingetOperationResult> UninstallAsync(string id, IProgress<WingetProgress>? progress = null, CancellationToken cancellationToken = default) => Ok("uninstall " + id, progress);
    public Task<IReadOnlyList<WingetSource>> ListSourcesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WingetSource>>(Sources);
    public Task<WingetOperationResult> RepairSourceAsync(bool elevated, CancellationToken cancellationToken = default) => Ok(elevated ? "repair-source elevated" : "repair-source");

    private Task<WingetOperationResult> Ok(string call, IProgress<WingetProgress>? progress = null)
    {
        Calls.Add(call);
        progress?.Report(new WingetProgress("Done", 100));
        return Task.FromResult(new WingetOperationResult(true, call + " ok", 0));
    }
}

public sealed class FakeWindowsUpdateService : IWindowsUpdateService
{
    public bool IsSupported { get; set; } = true;
    public WindowsUpdateStatus Status { get; set; } = new(true, false, null, false, DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(-7));
    public List<WindowsUpdateInfo> Available { get; } = [];
    public List<WindowsUpdateHistoryEntry> History { get; } = [];
    public List<string> Installed { get; } = [];

    public Task<WindowsUpdateStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(Status);
    public Task<IReadOnlyList<WindowsUpdateInfo>> SearchAsync(WindowsUpdateQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WindowsUpdateInfo>>([.. Available.Where(u => (query.IncludeDrivers || !u.IsDriver) && (query.IncludeOptional || !u.IsOptional))]);
    public Task<WindowsUpdateInstallResult> InstallAsync(IReadOnlyList<string> updateIds, IProgress<WindowsUpdateProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Installed.AddRange(updateIds);
        progress?.Report(new WindowsUpdateProgress("Installing", null, 100));
        var results = updateIds.Select(id => (id, Available.FirstOrDefault(u => u.UpdateId == id)?.Title ?? id, true, (string?)null)).ToList();
        return Task.FromResult(new WindowsUpdateInstallResult(true, false, results, "Installed"));
    }

    public Task<IReadOnlyList<WindowsUpdateHistoryEntry>> GetHistoryAsync(int max = 50, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WindowsUpdateHistoryEntry>>([.. History.Take(max)]);
}

public sealed class FakeTaskSchedulerService : ITaskSchedulerService
{
    public bool IsSupported { get; set; } = true;
    public List<ScheduledTaskInfo> Tasks { get; } = [];
    public List<string> Calls { get; } = [];

    public Task<IReadOnlyList<string>> GetFoldersAsync(string root = @"\", CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([.. Tasks.Select(t => t.Folder).Distinct()]);
    public Task<IReadOnlyList<ScheduledTaskInfo>> GetTasksAsync(string folder = @"\", bool recurse = false, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ScheduledTaskInfo>>([.. Tasks.Where(t => recurse ? t.Folder.StartsWith(folder, StringComparison.OrdinalIgnoreCase) : string.Equals(t.Folder, folder, StringComparison.OrdinalIgnoreCase))]);
    public Task<ScheduledTaskInfo?> GetTaskAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(Tasks.FirstOrDefault(t => t.Path == path));
    public Task RunAsync(string path, CancellationToken cancellationToken = default) => Record("run " + path);
    public Task StopAsync(string path, CancellationToken cancellationToken = default) => Record("stop " + path);
    public Task SetEnabledAsync(string path, bool enabled, CancellationToken cancellationToken = default) => Record((enabled ? "enable " : "disable ") + path);
    public Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        Tasks.RemoveAll(t => t.Path == path);
        return Record("delete " + path);
    }

    public Task<IReadOnlyList<ScheduledTaskRun>> GetHistoryAsync(string path, int max = 50, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ScheduledTaskRun>>([]);

    public Task<ScheduledTaskInfo> CreateAsync(ScheduledTaskDefinition definition, CancellationToken cancellationToken = default)
    {
        var path = definition.Folder.TrimEnd('\\') + @"\" + definition.Name;
        var info = new ScheduledTaskInfo(path, definition.Name, definition.Folder, true, "Ready", null, definition.Trigger.Start, null,
            definition.Description, Environment.UserName, [definition.Trigger.Kind.ToString()], [definition.Action.Program + " " + definition.Action.Arguments], definition.RunElevated);
        Tasks.RemoveAll(t => t.Path == path);
        Tasks.Add(info);
        Calls.Add("create " + path);
        return Task.FromResult(info);
    }

    public TaskTriggerSpec ParseSchedule(string text) => new(TaskTriggerKind.Daily, DateTimeOffset.Now);

    private Task Record(string call)
    {
        Calls.Add(call);
        return Task.CompletedTask;
    }
}

public sealed class FakeElevationBroker : IElevationBroker
{
    public bool IsSupported { get; set; } = true;
    public bool IsElevated { get; set; }
    public bool DeclineUac { get; set; }
    public List<ElevatedRequest> Requests { get; } = [];

    public Task<IReadOnlyList<ElevatedResponse>> RunAsync(IReadOnlyList<ElevatedRequest> batch, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (DeclineUac)
        {
            throw new OperationCanceledException("The user declined the UAC prompt.");
        }

        Requests.AddRange(batch);
        return Task.FromResult<IReadOnlyList<ElevatedResponse>>([.. batch.Select(b => new ElevatedResponse(true, b.Kind + " ok"))]);
    }
}

/// <summary>Records panel requests instead of showing Terminal.Gui; returns <see cref="NextResult"/>.</summary>
public sealed class FakePanelHost : IPanelHost
{
    public List<(string PanelId, string? Argument, string? Input)> Shown { get; } = [];
    public PanelResult? NextResult { get; set; }

    public PanelResult? Show(string panelId, string? argument = null, string? currentInput = null)
    {
        Shown.Add((panelId, argument, currentInput));
        return NextResult;
    }

    public PanelResult? Show(PanelDescriptor panel, string? argument = null, string? currentInput = null) => Show(panel.Id, argument, currentInput);
}
