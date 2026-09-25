using Pickle.Abstractions.Services;
using Pickle.Windows.TaskScheduler;

namespace Pickle.Windows;

/// <summary>Registered on non-Windows systems: report <c>IsSupported = false</c> and empty results, never throw.</summary>
internal sealed class UnsupportedWindowsUpdateService : IWindowsUpdateService
{
    public bool IsSupported => false;

    public Task<WindowsUpdateStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new WindowsUpdateStatus(false, false, "Windows Update is only available on Windows.", false, null, null));

    public Task<IReadOnlyList<WindowsUpdateInfo>> SearchAsync(WindowsUpdateQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WindowsUpdateInfo>>([]);

    public Task<WindowsUpdateInstallResult> InstallAsync(IReadOnlyList<string> updateIds, IProgress<WindowsUpdateProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new WindowsUpdateInstallResult(false, false, [], "Windows Update is only available on Windows."));

    public Task<IReadOnlyList<WindowsUpdateHistoryEntry>> GetHistoryAsync(int max = 50, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WindowsUpdateHistoryEntry>>([]);
}

internal sealed class UnsupportedTaskSchedulerService : ITaskSchedulerService
{
    private const string Message = "Task Scheduler is only available on Windows.";

    public bool IsSupported => false;

    public Task<IReadOnlyList<string>> GetFoldersAsync(string root = @"\", CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<IReadOnlyList<ScheduledTaskInfo>> GetTasksAsync(string folder = @"\", bool recurse = false, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ScheduledTaskInfo>>([]);

    public Task<ScheduledTaskInfo?> GetTaskAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult<ScheduledTaskInfo?>(null);

    public Task RunAsync(string path, CancellationToken cancellationToken = default) => throw new PlatformNotSupportedException(Message);

    public Task StopAsync(string path, CancellationToken cancellationToken = default) => throw new PlatformNotSupportedException(Message);

    public Task SetEnabledAsync(string path, bool enabled, CancellationToken cancellationToken = default) => throw new PlatformNotSupportedException(Message);

    public Task DeleteAsync(string path, CancellationToken cancellationToken = default) => throw new PlatformNotSupportedException(Message);

    public Task<IReadOnlyList<ScheduledTaskRun>> GetHistoryAsync(string path, int max = 50, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ScheduledTaskRun>>([]);

    public Task<ScheduledTaskInfo> CreateAsync(ScheduledTaskDefinition definition, CancellationToken cancellationToken = default) =>
        throw new PlatformNotSupportedException(Message);

    public TaskTriggerSpec ParseSchedule(string text) => ScheduleParser.Parse(text);
}

public sealed class UnsupportedToolInstaller : IToolInstaller
{
    public bool IsSupported => false;

    public IReadOnlyList<ToolPackage> TemporaryInstalls => [];

    public ToolPackage? Find(string command) => ToolCatalog.Find(command);

    public Task<ToolInstallResult> InstallAsync(ToolPackage package, ToolInstallOptions options, IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ToolInstallResult(false, "Installing tools needs winget (Windows)."));

    public Task<ToolInstallResult> RemoveTemporaryAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ToolInstallResult(true, "No temporary tools to remove."));
}
