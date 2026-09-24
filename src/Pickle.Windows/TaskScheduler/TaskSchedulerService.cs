using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32.TaskScheduler;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using SysTask = System.Threading.Tasks.Task;

namespace Pickle.Windows.TaskScheduler;

/// <summary>Task Scheduler 2.0 via the TaskScheduler (dahall) wrapper. Elevated tasks are registered through the broker.</summary>
[SupportedOSPlatform("windows")]
public sealed class TaskSchedulerService : ITaskSchedulerService
{
    private readonly IPickleLogger _log;
    private readonly Func<IElevationBroker?> _broker;

    public TaskSchedulerService(IPickleContext context)
        : this(context.Log, () => context.Services.Get<IElevationBroker>())
    {
    }

    internal TaskSchedulerService(IPickleLogger log, Func<IElevationBroker?> broker)
    {
        _log = log;
        _broker = broker;
    }

    public bool IsSupported => true;

    public Task<IReadOnlyList<string>> GetFoldersAsync(string root = @"\", CancellationToken cancellationToken = default) =>
        SysTask.Run<IReadOnlyList<string>>(() =>
        {
            using var service = new TaskService();
            using var folder = service.GetFolder(NormalizeFolder(root));
            if (folder is null)
            {
                return [];
            }

            var paths = new List<string> { folder.Path };
            try
            {
                paths.AddRange(folder.EnumerateFolders(null, true).Select(f => f.Path));
            }
            catch (UnauthorizedAccessException ex)
            {
                _log.Warn("tasks", "some task folders are not accessible", ex);
            }

            return [.. paths.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
        }, cancellationToken);

    public Task<IReadOnlyList<ScheduledTaskInfo>> GetTasksAsync(string folder = @"\", bool recurse = false, CancellationToken cancellationToken = default) =>
        SysTask.Run<IReadOnlyList<ScheduledTaskInfo>>(() =>
        {
            using var service = new TaskService();
            using var taskFolder = service.GetFolder(NormalizeFolder(folder));
            if (taskFolder is null)
            {
                return [];
            }

            var list = new List<ScheduledTaskInfo>();
            foreach (var task in taskFolder.EnumerateTasks(null, recurse))
            {
                using (task)
                {
                    list.Add(TaskMapper.Map(task));
                }
            }

            return list;
        }, cancellationToken);

    public Task<ScheduledTaskInfo?> GetTaskAsync(string path, CancellationToken cancellationToken = default) =>
        SysTask.Run(() =>
        {
            using var service = new TaskService();
            using var task = service.GetTask(path);
            return task is null ? null : TaskMapper.Map(task);
        }, cancellationToken);

    public SysTask RunAsync(string path, CancellationToken cancellationToken = default) =>
        WithTask(path, "run", task => task.Run(), cancellationToken);

    public SysTask StopAsync(string path, CancellationToken cancellationToken = default) =>
        WithTask(path, "stop", task => task.Stop(), cancellationToken);

    public SysTask SetEnabledAsync(string path, bool enabled, CancellationToken cancellationToken = default) =>
        WithTask(path, enabled ? "enable" : "disable", task => task.Enabled = enabled, cancellationToken);

    public SysTask DeleteAsync(string path, CancellationToken cancellationToken = default) =>
        WithTask(path, "delete", task => task.Folder.DeleteTask(task.Name, false), cancellationToken);

    public Task<IReadOnlyList<ScheduledTaskRun>> GetHistoryAsync(string path, int max = 50, CancellationToken cancellationToken = default) =>
        SysTask.Run<IReadOnlyList<ScheduledTaskRun>>(() =>
        {
            var runs = new List<ScheduledTaskRun>();
            try
            {
                var log = new TaskEventLog(path) { EnumerateInReverse = true };
                foreach (var entry in log)
                {
                    if (runs.Count >= max || cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    int? code = int.TryParse(entry.DataValues["ResultCode"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
                    runs.Add(new ScheduledTaskRun(
                        entry.TimeCreated is { } time ? new DateTimeOffset(time) : DateTimeOffset.MinValue,
                        entry.StandardEventId.ToString(),
                        code,
                        entry.TaskCategory));
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or System.Diagnostics.Eventing.Reader.EventLogException or NotSupportedException)
            {
                _log.Warn("tasks", $"history for {path} is unavailable", ex);
            }

            return runs;
        }, cancellationToken);

    public async Task<ScheduledTaskInfo> CreateAsync(ScheduledTaskDefinition definition, CancellationToken cancellationToken = default)
    {
        var needsElevation = (definition.RunElevated || definition.Trigger.Kind == TaskTriggerKind.AtStartup) && !Environment.IsPrivilegedProcess;
        var normalized = definition with { Folder = NormalizeFolder(definition.Folder), RunElevated = definition.RunElevated || needsElevation };
        TaskDefinitionCodec.Validate(normalized, normalized.RunElevated);
        var path = normalized.Folder.TrimEnd('\\') + @"\" + normalized.Name;
        _log.Info("tasks", $"create {path} trigger={normalized.Trigger.Kind} elevated={normalized.RunElevated}");

        if (!needsElevation)
        {
            return await SysTask.Run(() => TaskRegistrar.Register(normalized), cancellationToken).ConfigureAwait(false);
        }

        if (_broker() is not { IsSupported: true } broker)
        {
            throw new InvalidOperationException("Creating an elevated task needs the elevation broker, which is unavailable.");
        }

        // Create the folder unelevated first so it stays writable by the user for later, unelevated tasks.
        await SysTask.Run(() =>
        {
            try
            {
                using var service = new TaskService();
                TaskRegistrar.EnsureFolder(service, normalized.Folder).Dispose();
            }
            catch (UnauthorizedAccessException ex)
            {
                _log.Warn("tasks", "could not pre-create the task folder unelevated", ex);
            }
        }, cancellationToken).ConfigureAwait(false);

        var responses = await broker.RunAsync(
            [new ElevatedRequest(ElevatedOperationKind.TaskRegisterElevated, [TaskDefinitionCodec.Serialize(normalized)])],
            null,
            cancellationToken).ConfigureAwait(false);
        var response = responses.FirstOrDefault();
        if (response is not { Success: true })
        {
            throw new InvalidOperationException(response?.Message ?? "The elevated helper returned no result.");
        }

        return await GetTaskAsync(path, cancellationToken).ConfigureAwait(false)
            ?? new ScheduledTaskInfo(path, normalized.Name, normalized.Folder, true, "Ready", null, normalized.Trigger.Start, null,
                normalized.Description, Environment.UserName, [ScheduleParser.Describe(normalized.Trigger)],
                [$"{normalized.Action.Program} {normalized.Action.Arguments}".TrimEnd()], true);
    }

    public TaskTriggerSpec ParseSchedule(string text) => ScheduleParser.Parse(text);

    internal static string NormalizeFolder(string folder)
    {
        var trimmed = string.IsNullOrWhiteSpace(folder) ? @"\" : folder.Trim().Replace('/', '\\');
        if (!trimmed.StartsWith('\\'))
        {
            trimmed = @"\" + trimmed;
        }

        return trimmed.Length > 1 ? trimmed.TrimEnd('\\') : trimmed;
    }

    private SysTask WithTask(string path, string verb, Action<Microsoft.Win32.TaskScheduler.Task> action, CancellationToken cancellationToken) =>
        SysTask.Run(() =>
        {
            _log.Info("tasks", $"{verb} {path}");
            using var service = new TaskService();
            using var task = service.GetTask(path) ?? throw new InvalidOperationException($"Task '{path}' was not found.");
            action(task);
        }, cancellationToken);
}
