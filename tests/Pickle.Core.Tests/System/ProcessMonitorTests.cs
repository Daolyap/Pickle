using System.Diagnostics;
using Pickle.Abstractions.Services;
using Pickle.Core.Commands;
using Pickle.Core.SystemMonitoring;

namespace Pickle.Core.Tests.SystemMonitoring;

public sealed class ProcessMonitorTests : IDisposable
{
    private static readonly DateTimeOffset Boot = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pickle-proc-tests", Guid.NewGuid().ToString("N")[..10]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public void CpuPercentIsTheShareOfAllProcessorsSinceThePreviousSample()
    {
        var source = new FakeSource();
        var now = TimeSpan.Zero;
        var monitor = new ProcessMonitor(source, () => now, processorCount: 2);
        source.Processes = [Raw(10, "busy", 1.0), Raw(11, "idle", 5.0)];
        source.Cpu = (100, 1000);

        var first = monitor.Sample();
        now = TimeSpan.FromSeconds(1);
        source.Processes = [Raw(10, "busy", 2.0), Raw(11, "idle", 5.0), Raw(12, "new", 0.5)];
        source.Cpu = (250, 1200);
        var second = monitor.Sample();

        Assert.All(first.Processes, p => Assert.Equal(0, p.CpuPercent));
        Assert.Equal(50, second.Processes.Single(p => p.Id == 10).CpuPercent, 3);
        Assert.Equal(0, second.Processes.Single(p => p.Id == 11).CpuPercent);
        Assert.Equal(0, second.Processes.Single(p => p.Id == 12).CpuPercent);
        Assert.Equal(75, second.CpuPercent, 3);
        Assert.Equal((4L << 30, 16L << 30), (second.MemoryUsed, second.MemoryTotal));
    }

    [Fact]
    public void AReusedProcessIdStartsFromZero()
    {
        var source = new FakeSource();
        var now = TimeSpan.Zero;
        var monitor = new ProcessMonitor(source, () => now, processorCount: 1);
        source.Processes = [Raw(10, "old", 1.0)];
        monitor.Sample();
        now = TimeSpan.FromSeconds(1);
        source.Processes = [Raw(10, "reused", 1.5, start: Boot.AddHours(1))];

        Assert.Equal(0, monitor.Sample().Processes.Single().CpuPercent);
    }

    [Fact]
    public void WithoutMachineTotalsTheCpuIsTheSumOfProcesses()
    {
        var source = new FakeSource { Cpu = null };
        var now = TimeSpan.Zero;
        var monitor = new ProcessMonitor(source, () => now, processorCount: 4);
        source.Processes = [Raw(1, "a", 1), Raw(2, "b", 1)];
        monitor.Sample();
        now = TimeSpan.FromSeconds(1);
        source.Processes = [Raw(1, "a", 2), Raw(2, "b", 1.4)];

        Assert.Equal(35, monitor.Sample().CpuPercent, 3);
    }

    [Fact]
    public async Task ActionsRefuseAProcessWhoseIdWasReused()
    {
        var source = new FakeSource { StartTimes = { [Environment.ProcessId + 1] = Boot.AddDays(1) } };
        var monitor = new ProcessMonitor(source);
        var stale = new ProcessSample(Environment.ProcessId + 1, "gone", 0, 0, 1, Boot);

        var kill = await monitor.KillAsync(stale, entireTree: false);
        var priority = await monitor.SetPriorityAsync(stale, ProcessPriority.Idle);

        Assert.False(kill.Success);
        Assert.Contains("no longer running", kill.Message, StringComparison.Ordinal);
        Assert.False(priority.Success);
        Assert.Null(await monitor.GetDetailsAsync(stale));
    }

    [Fact]
    public async Task KillRefusesPickleItself()
    {
        var monitor = new ProcessMonitor(new FakeSource());
        var result = await monitor.KillAsync(new ProcessSample(Environment.ProcessId, "pickle", 0, 0, 1, null), entireTree: true);
        Assert.False(result.Success);
        Assert.Contains("Pickle itself", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealSnapshotContainsThisProcessAndDetailsCanBeRead()
    {
        var monitor = new ProcessMonitor();

        var snapshot = await monitor.SampleAsync();
        var self = snapshot.Processes.Single(p => p.Id == Environment.ProcessId);
        var details = await monitor.GetDetailsAsync(self);

        Assert.True(snapshot.Processes.Count > 1);
        Assert.InRange(snapshot.CpuPercent, 0, 100);
        Assert.True(self.WorkingSet > 0);
        Assert.True(self.Threads > 0);
        Assert.NotNull(self.StartTime);
        Assert.NotNull(details);
        Assert.Equal(self.Id, details.Id);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
        {
            Assert.True(snapshot.MemoryTotal > 0);
            Assert.False(string.IsNullOrEmpty(details.Path));
            Assert.False(string.IsNullOrEmpty(details.CommandLine));
        }
    }

    [Fact]
    public async Task KillEndsARealChildProcessAndPriorityCanBeLowered()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "uses /bin/sleep");
        var sleep = ExecutableLocator.Find("sleep");
        Assert.SkipWhen(sleep is null, "sleep is not installed");
        using var child = Process.Start(new ProcessStartInfo(sleep!) { ArgumentList = { "30" }, UseShellExecute = false })!;
        var monitor = new ProcessMonitor();
        var sample = (await monitor.SampleAsync()).Processes.Single(p => p.Id == child.Id);

        var lowered = await monitor.SetPriorityAsync(sample, ProcessPriority.BelowNormal);
        var details = await monitor.GetDetailsAsync(sample);
        var killed = await monitor.KillAsync(sample, entireTree: true);

        Assert.True(lowered.Success, lowered.Message);
        Assert.Equal(ProcessPriority.BelowNormal, details?.Priority);
        Assert.True(killed.Success, killed.Message);
        Assert.True(child.WaitForExit(5000));
    }

    [Fact]
    public void ProcFsSourceReadsProcessesNamesUsersAndTotals()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "stat"), "cpu  100 0 100 800 0 0 0 0 0 0\nbtime 1700000000\n");
        File.WriteAllText(Path.Combine(_root, "meminfo"), "MemTotal: 1000 kB\nMemAvailable: 250 kB\n");
        var passwd = Path.Combine(_root, "passwd");
        File.WriteAllText(passwd, "root:x:0:0::/root:/bin/sh\nalice:x:1000:1000::/home/alice:/bin/sh\n");
        Proc(1, "1 (init) S 0 1 1 0 -1 0 0 0 0 0 10 5 0 0 20 0 1 0 100 0 50", uid: 0);
        Proc(2, "2 (kthreadd) S 0 0 0 0 -1 0 0 0 0 0 0 0 0 0 20 0 1 0 1 0 0", uid: 0);
        Proc(3, "3 (kworker/0:1) I 2 0 0 0 -1 0 0 0 0 0 0 0 0 0 20 0 1 0 2 0 0", uid: 0);
        Proc(400, "400 (a-very-long-pro) R 1 400 400 0 -1 0 0 0 0 0 200 100 0 0 20 0 12 0 5000 0 1000", uid: 1000, cmdline: "/usr/bin/a-very-long-process-name\0--flag\0");
        Directory.CreateDirectory(Path.Combine(_root, "self"));

        var source = new ProcFsProcessSource(_root, passwd, pageSize: 4096);
        var processes = source.ReadProcesses().OrderBy(p => p.Id).ToList();

        Assert.Equal([1, 400], processes.Select(p => p.Id));
        var app = processes[1];
        Assert.Equal("a-very-long-process-name", app.Name);
        Assert.Equal("alice", app.User);
        Assert.Equal(TimeSpan.FromSeconds(3), app.CpuTime);
        Assert.Equal(1000L * 4096, app.WorkingSet);
        Assert.Equal(12, app.Threads);
        Assert.Equal(1, app.ParentId);
        Assert.Equal(Boot.AddSeconds(50), app.StartTime);
        Assert.Equal("root", processes[0].User);
        Assert.Equal((200L, 1000L), source.ReadCpuTimes());
        Assert.Equal((750L * 1024, 1000L * 1024), source.ReadMemory());
        Assert.Equal(Boot.AddSeconds(50), source.StartTimeOf(400));

        var details = source.ReadDetails(new ProcessSample(400, app.Name, 0, app.WorkingSet, 12, app.StartTime, "alice", 1));
        Assert.NotNull(details);
        Assert.Equal("/usr/bin/a-very-long-process-name --flag", details.CommandLine);
        Assert.Equal("init", details.ParentName);
    }

    private static RawProcess Raw(int id, string name, double cpuSeconds, DateTimeOffset? start = null) =>
        new(id, name, TimeSpan.FromSeconds(cpuSeconds), 1 << 20, 1, start ?? Boot, null, null);

    private void Proc(int pid, string stat, int uid, string? cmdline = null)
    {
        var dir = Path.Combine(_root, pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "stat"), stat + "\n");
        File.WriteAllText(Path.Combine(dir, "status"), $"Name:\tx\nUid:\t{uid}\t{uid}\t{uid}\t{uid}\n");
        File.WriteAllText(Path.Combine(dir, "cmdline"), cmdline ?? string.Empty);
    }

    private sealed class FakeSource : IProcessSource
    {
        public IReadOnlyList<RawProcess> Processes { get; set; } = [];

        public (long Busy, long Total)? Cpu { get; set; } = (0, 0);

        public Dictionary<int, DateTimeOffset> StartTimes { get; } = [];

        public IReadOnlyList<RawProcess> ReadProcesses() => Processes;

        public (long Busy, long Total)? ReadCpuTimes() => Cpu;

        public (long Used, long Total)? ReadMemory() => (4L << 30, 16L << 30);

        public DateTimeOffset? StartTimeOf(int processId) => StartTimes.TryGetValue(processId, out var start) ? start : null;

        public ProcessDetails? ReadDetails(ProcessSample process) => null;
    }
}
