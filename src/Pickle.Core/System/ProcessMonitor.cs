using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Pickle.Abstractions.Services;

namespace Pickle.Core.SystemMonitoring;

/// <summary>A process as read from the OS: cumulative CPU time, no rates yet.</summary>
internal readonly record struct RawProcess(int Id, string Name, TimeSpan CpuTime, long WorkingSet, int Threads, DateTimeOffset? StartTime, string? User, int? ParentId);

/// <summary>Where <see cref="ProcessMonitor"/> reads processes and machine totals from.</summary>
internal interface IProcessSource
{
    IReadOnlyList<RawProcess> ReadProcesses();

    /// <summary>Cumulative busy and total CPU time (any unit); null when unavailable.</summary>
    (long Busy, long Total)? ReadCpuTimes();

    /// <summary>Used and total physical memory in bytes; null when unknown.</summary>
    (long Used, long Total)? ReadMemory();

    DateTimeOffset? StartTimeOf(int processId);

    ProcessDetails? ReadDetails(ProcessSample process);
}

/// <summary>
/// <see cref="IProcessMonitor"/>: <c>/proc</c> on Linux, <see cref="Process"/> (plus a few Win32 calls) elsewhere.
/// Per-process CPU % is the share of all processors used since the previous sample, like Windows Task Manager.
/// </summary>
public sealed class ProcessMonitor : IProcessMonitor
{
    private static readonly TimeSpan WarmUp = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(10);

    private readonly IProcessSource _source;
    private readonly Func<TimeSpan> _now;
    private readonly int _processorCount;
    private readonly object _gate = new();
    private Dictionary<(int Id, long Start), TimeSpan> _lastCpu = [];
    private (long Busy, long Total)? _lastSystem;
    private TimeSpan? _lastTime;

    public ProcessMonitor()
        : this(CreateSource())
    {
    }

    internal ProcessMonitor(IProcessSource source, Func<TimeSpan>? now = null, int processorCount = 0)
    {
        _source = source;
        var clock = Stopwatch.StartNew();
        _now = now ?? (() => clock.Elapsed);
        _processorCount = processorCount > 0 ? processorCount : Environment.ProcessorCount;
    }

    public Task<ProcessSnapshot> SampleAsync(CancellationToken cancellationToken = default) => Task.Run(
        async () =>
        {
            bool stale;
            lock (_gate)
            {
                stale = _lastTime is not { } last || _now() - last > StaleAfter;
            }

            // Without a recent previous sample every CPU % would be 0: take a short baseline first.
            if (stale)
            {
                Sample();
                await Task.Delay(WarmUp, cancellationToken).ConfigureAwait(false);
            }

            return Sample();
        },
        cancellationToken);

    public Task<ProcessDetails?> GetDetailsAsync(ProcessSample process, CancellationToken cancellationToken = default) =>
        Task.Run(() => IsSameProcess(process) ? _source.ReadDetails(process) : null, cancellationToken);

    public Task<ProcessActionResult> KillAsync(ProcessSample process, bool entireTree, CancellationToken cancellationToken = default) => Task.Run(
        () =>
        {
            if (process.Id == Environment.ProcessId)
            {
                return new ProcessActionResult(false, "That is Pickle itself — use 'exit' to close the shell.");
            }

            return Act(process, p =>
            {
                p.Kill(entireTree);
                return $"Ended {Describe(process)}{(entireTree ? " and its child processes" : string.Empty)}.";
            });
        },
        cancellationToken);

    public Task<ProcessActionResult> SetPriorityAsync(ProcessSample process, ProcessPriority priority, CancellationToken cancellationToken = default) => Task.Run(
        () => Act(process, p =>
        {
            p.PriorityClass = priority switch
            {
                ProcessPriority.Idle => ProcessPriorityClass.Idle,
                ProcessPriority.BelowNormal => ProcessPriorityClass.BelowNormal,
                ProcessPriority.AboveNormal => ProcessPriorityClass.AboveNormal,
                ProcessPriority.High => ProcessPriorityClass.High,
                _ => ProcessPriorityClass.Normal,
            };
            return $"{Describe(process)} now runs at {PriorityName(priority)} priority.";
        }),
        cancellationToken);

    internal static string PriorityName(ProcessPriority priority) => priority switch
    {
        ProcessPriority.BelowNormal => "below normal",
        ProcessPriority.AboveNormal => "above normal",
        _ => priority.ToString().ToLowerInvariant(),
    };

    internal static ProcessPriority? ReadPriority(Process process)
    {
        try
        {
            return process.PriorityClass switch
            {
                ProcessPriorityClass.Idle => ProcessPriority.Idle,
                ProcessPriorityClass.BelowNormal => ProcessPriority.BelowNormal,
                ProcessPriorityClass.AboveNormal => ProcessPriority.AboveNormal,
                ProcessPriorityClass.High or ProcessPriorityClass.RealTime => ProcessPriority.High,
                _ => ProcessPriority.Normal,
            };
        }
        catch (Exception ex) when (IsInaccessible(ex))
        {
            return null;
        }
    }

    internal static bool IsInaccessible(Exception ex) =>
        ex is Win32Exception or InvalidOperationException or NotSupportedException or UnauthorizedAccessException or ArgumentException or IOException;

    internal ProcessSnapshot Sample()
    {
        lock (_gate)
        {
            var processes = _source.ReadProcesses();
            var system = _source.ReadCpuTimes();
            var memory = _source.ReadMemory();
            var now = _now();
            var capacity = _lastTime is { } last ? (now - last).Ticks * (double)_processorCount : 0;
            var next = new Dictionary<(int Id, long Start), TimeSpan>(processes.Count);
            var samples = new List<ProcessSample>(processes.Count);
            var sum = 0.0;
            foreach (var p in processes)
            {
                var key = (p.Id, p.StartTime?.UtcTicks ?? 0);
                var cpu = 0.0;
                if (capacity > 0 && _lastCpu.TryGetValue(key, out var previous) && p.CpuTime > previous)
                {
                    cpu = Math.Min(100, (p.CpuTime - previous).Ticks * 100.0 / capacity);
                }

                next[key] = p.CpuTime;
                sum += cpu;
                samples.Add(new ProcessSample(p.Id, p.Name, cpu, p.WorkingSet, p.Threads, p.StartTime, p.User, p.ParentId));
            }

            var total = system is { } s && _lastSystem is { } ls && s.Total > ls.Total
                ? Math.Clamp((s.Busy - ls.Busy) * 100.0 / (s.Total - ls.Total), 0, 100)
                : Math.Min(100, sum);
            _lastCpu = next;
            _lastSystem = system;
            _lastTime = now;
            return new ProcessSnapshot(DateTimeOffset.Now, total, memory?.Used ?? 0, memory?.Total ?? 0, samples);
        }
    }

    private static IProcessSource CreateSource() =>
        OperatingSystem.IsLinux() && ProcFsProcessSource.IsAvailable() ? new ProcFsProcessSource() : new ProcessApiSource();

    private static string Describe(ProcessSample process) => $"{process.Name} ({process.Id})";

    // The id may have been reused since the row was sampled: never act on a different process.
    private bool IsSameProcess(ProcessSample process) =>
        process.StartTime is not { } expected ||
        _source.StartTimeOf(process.Id) is not { } actual ||
        Math.Abs((actual - expected).TotalSeconds) < 1;

    private ProcessActionResult Act(ProcessSample sample, Func<Process, string> action)
    {
        var gone = new ProcessActionResult(false, $"{Describe(sample)} is no longer running.");
        if (!IsSameProcess(sample))
        {
            return gone;
        }

        try
        {
            using var process = Process.GetProcessById(sample.Id);
            return new ProcessActionResult(true, action(process));
        }
        catch (ArgumentException)
        {
            return gone;
        }
        catch (InvalidOperationException)
        {
            return gone;
        }
        catch (AggregateException ex)
        {
            return new ProcessActionResult(false, $"Could not end every process in the tree: {ex.InnerExceptions.FirstOrDefault()?.Message ?? ex.Message}");
        }
        catch (Exception ex) when (ex is Win32Exception or NotSupportedException or UnauthorizedAccessException)
        {
            return new ProcessActionResult(false, $"{Describe(sample)}: {ex.Message}");
        }
    }
}

/// <summary>Linux: one read of <c>/proc/&lt;pid&gt;/stat</c> per process; names and users are cached per process.</summary>
internal sealed class ProcFsProcessSource : IProcessSource
{
    private readonly string _proc;
    private readonly string _passwd;
    private readonly int _pageSize;
    private Dictionary<int, string>? _users;
    private DateTimeOffset? _bootTime;
    private Dictionary<(int Id, long Start), (string Name, string? User)> _identities = [];

    public ProcFsProcessSource(string proc = "/proc", string passwd = "/etc/passwd", int pageSize = 0)
    {
        _proc = proc;
        _passwd = passwd;
        _pageSize = pageSize > 0 ? pageSize : Environment.SystemPageSize;
    }

    public static bool IsAvailable(string proc = "/proc") => File.Exists(Path.Join(proc, "stat"));

    public IReadOnlyList<RawProcess> ReadProcesses()
    {
        var boot = BootTime();
        var result = new List<RawProcess>();
        var identities = new Dictionary<(int Id, long Start), (string Name, string? User)>();
        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(_proc).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return result;
        }

        foreach (var directory in directories)
        {
            if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || ReadStat(pid) is not { } stat)
            {
                continue;
            }

            // Kernel threads (kthreadd and its children), as ps/htop hide them by default.
            if (stat.Pid == 2 || stat.ParentId == 2)
            {
                continue;
            }

            var key = (pid, stat.StartTicks);
            if (!_identities.TryGetValue(key, out var identity))
            {
                identity = (FullName(pid, stat.Comm), User(pid));
            }

            identities[key] = identity;
            result.Add(new RawProcess(pid, identity.Name, Ticks(stat.UserTicks + stat.SystemTicks), stat.RssPages * _pageSize, stat.Threads, StartTime(stat, boot), identity.User, stat.ParentId));
        }

        _identities = identities;
        return result;
    }

    public (long Busy, long Total)? ReadCpuTimes() => Read("stat") is { } text ? ProcFs.ParseCpu(text) : null;

    public (long Used, long Total)? ReadMemory() => Read("meminfo") is { } text ? ProcFs.ParseMemInfo(text) : null;

    public DateTimeOffset? StartTimeOf(int processId) => ReadStat(processId) is { } stat ? StartTime(stat, BootTime()) : null;

    public ProcessDetails? ReadDetails(ProcessSample process)
    {
        if (ReadStat(process.Id) is not { } stat)
        {
            return null;
        }

        string? path = null;
        try
        {
            path = new FileInfo(Path.Join(_proc, process.Id.ToString(CultureInfo.InvariantCulture), "exe")).LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        var commandLine = Read(process.Id, "cmdline")?.Replace('\0', ' ').Trim();
        var parentName = stat.ParentId > 0 ? ReadStat(stat.ParentId)?.Comm : null;
        ProcessPriority? priority = null;
        long? privateMemory = null;
        try
        {
            using var p = Process.GetProcessById(process.Id);
            priority = ProcessMonitor.ReadPriority(p);
            privateMemory = p.PrivateMemorySize64;
        }
        catch (Exception ex) when (ProcessMonitor.IsInaccessible(ex))
        {
        }

        return new ProcessDetails(
            process.Id,
            process.Name,
            path,
            string.IsNullOrEmpty(commandLine) ? null : commandLine,
            stat.ParentId,
            parentName,
            process.User,
            StartTime(stat, BootTime()),
            priority,
            stat.RssPages * _pageSize,
            privateMemory,
            stat.Threads,
            Ticks(stat.UserTicks + stat.SystemTicks));
    }

    private static TimeSpan Ticks(long clockTicks) => TimeSpan.FromTicks(clockTicks * (TimeSpan.TicksPerSecond / ProcFs.ClockTicksPerSecond));

    private static DateTimeOffset? StartTime(ProcStat stat, DateTimeOffset? boot) => boot?.Add(Ticks(stat.StartTicks));

    private ProcStat? ReadStat(int pid) => Read(pid, "stat") is { } text ? ProcFs.ParseStat(text) : null;

    private DateTimeOffset? BootTime() => _bootTime ??= Read("stat") is { } text ? ProcFs.ParseBootTime(text) : null;

    // comm is cut at 15 characters; the full name is the start of argv[0].
    private string FullName(int pid, string comm)
    {
        if (comm.Length < 15 || Read(pid, "cmdline") is not { Length: > 0 } cmdline)
        {
            return comm;
        }

        var argv0 = cmdline.Split('\0')[0];
        var name = argv0[(argv0.LastIndexOf('/') + 1)..];
        return name.StartsWith(comm, StringComparison.Ordinal) ? name : comm;
    }

    private string? User(int pid)
    {
        if (Read(pid, "status") is not { } status || ProcFs.ParseUid(status) is not { } uid)
        {
            return null;
        }

        _users ??= File.Exists(_passwd) ? ProcFs.ParsePasswd(ReadFile(_passwd) ?? string.Empty) : [];
        return _users.TryGetValue(uid, out var name) ? name : uid.ToString(CultureInfo.InvariantCulture);
    }

    private string? Read(string file) => ReadFile(Path.Join(_proc, file));

    private string? Read(int pid, string file) => ReadFile(Path.Join(_proc, pid.ToString(CultureInfo.InvariantCulture), file));

    private static string? ReadFile(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Windows, macOS and anything without <c>/proc</c>: <see cref="Process"/>, plus Win32 totals and owners on Windows.</summary>
internal sealed class ProcessApiSource : IProcessSource
{
    private Dictionary<(int Id, long Start), string?> _users = [];

    public IReadOnlyList<RawProcess> ReadProcesses()
    {
        var result = new List<RawProcess>();
        var users = new Dictionary<(int Id, long Start), string?>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                // The Windows "Idle" process: its CPU time is idle time.
                if (process.Id == 0 || Try(() => process.ProcessName) is not { } name)
                {
                    continue;
                }

                var start = TryValue(() => (DateTimeOffset)process.StartTime);
                string? user = null;
                if (OperatingSystem.IsWindows())
                {
                    var key = (process.Id, start?.UtcTicks ?? 0);
                    if (!_users.TryGetValue(key, out user))
                    {
                        user = WindowsSystemNative.UserName(process.Id);
                    }

                    users[key] = user;
                }

                result.Add(new RawProcess(
                    process.Id,
                    name,
                    TryValue(() => process.TotalProcessorTime) ?? TimeSpan.Zero,
                    TryValue(() => process.WorkingSet64) ?? 0,
                    TryValue(() => process.Threads.Count) ?? 0,
                    start,
                    user,
                    null));
            }
        }

        _users = users;
        return result;
    }

    public (long Busy, long Total)? ReadCpuTimes() => OperatingSystem.IsWindows() ? WindowsSystemNative.SystemTimes() : null;

    public (long Used, long Total)? ReadMemory() => OperatingSystem.IsWindows() ? WindowsSystemNative.Memory() : null;

    public DateTimeOffset? StartTimeOf(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return TryValue(() => (DateTimeOffset)process.StartTime);
        }
        catch (Exception ex) when (ProcessMonitor.IsInaccessible(ex))
        {
            return null;
        }
    }

    public ProcessDetails? ReadDetails(ProcessSample sample)
    {
        try
        {
            using var process = Process.GetProcessById(sample.Id);
            string? path;
            string? commandLine = null;
            int? parentId = null;
            if (OperatingSystem.IsWindows())
            {
                path = WindowsSystemNative.ImagePath(sample.Id);
                commandLine = WindowsSystemNative.CommandLine(sample.Id);
                parentId = WindowsSystemNative.ParentId(sample.Id);
            }
            else
            {
                path = Try(() => process.MainModule?.FileName);
            }

            return new ProcessDetails(
                sample.Id,
                sample.Name,
                path,
                string.IsNullOrWhiteSpace(commandLine) ? null : commandLine,
                parentId,
                parentId is { } parent ? NameOf(parent) : null,
                sample.User,
                sample.StartTime,
                ProcessMonitor.ReadPriority(process),
                TryValue(() => process.WorkingSet64) ?? sample.WorkingSet,
                TryValue(() => process.PrivateMemorySize64),
                TryValue(() => process.Threads.Count) ?? sample.Threads,
                TryValue(() => process.TotalProcessorTime));
        }
        catch (Exception ex) when (ProcessMonitor.IsInaccessible(ex))
        {
            return null;
        }
    }

    private static string? NameOf(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ProcessMonitor.IsInaccessible(ex))
        {
            return null;
        }
    }

    private static string? Try(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ProcessMonitor.IsInaccessible(ex))
        {
            return null;
        }
    }

    private static T? TryValue<T>(Func<T> read)
        where T : struct
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ProcessMonitor.IsInaccessible(ex))
        {
            return null;
        }
    }
}
