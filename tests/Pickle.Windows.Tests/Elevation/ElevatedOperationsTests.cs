using Pickle.Abstractions.Services;
using Pickle.Windows.Elevation;
using Pickle.Windows.TaskScheduler;

namespace Pickle.Windows.Tests.Elevation;

public class ElevatedOperationsTests
{
    private static readonly string Program = OperatingSystem.IsWindows() ? @"C:\Program Files\Pickle\pickle.exe" : "/opt/pickle/pickle.exe";

    internal static ScheduledTaskDefinition Task(string folder = @"\Pickle", string? program = null) => new(
        "nightly",
        new TaskTriggerSpec(TaskTriggerKind.Daily, new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero)),
        new TaskActionSpec(program ?? Program, "-NoLogo -c \"pk upgrade --yes\""),
        folder,
        "Pickle: pk upgrade",
        RunElevated: true);

    [Theory]
    [InlineData(ElevatedOperationKind.WingetUpgrade, new[] { "Git.Git", "Microsoft.VCRedist.2015+.x64" })]
    [InlineData(ElevatedOperationKind.WingetUpgrade, new[] { "--all" })]
    [InlineData(ElevatedOperationKind.WingetInstall, new[] { "7zip.7zip" })]
    [InlineData(ElevatedOperationKind.WindowsUpdateInstall, new[] { "0A1B2C3D-4E5F-6071-8293-A4B5C6D7E8F9", "{0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f0}" })]
    [InlineData(ElevatedOperationKind.WingetRepairSource, new string[0])]
    [InlineData(ElevatedOperationKind.WingetUninstall, new[] { "Git.Git", "Microsoft.VCRedist.2015+.x64" })]
    public void AcceptsAllowlistedRequests(ElevatedOperationKind kind, string[] args)
    {
        var op = ElevatedOperations.Validate(new ElevatedRequest(kind, args));
        Assert.Equal(kind, op.Kind);
    }

    [Theory]
    [InlineData(ElevatedOperationKind.WingetUpgrade, new[] { "Git.Git; calc.exe" })]
    [InlineData(ElevatedOperationKind.WingetUpgrade, new[] { "Git Git" })]
    [InlineData(ElevatedOperationKind.WingetUpgrade, new[] { "-x" })]
    [InlineData(ElevatedOperationKind.WingetUpgrade, new[] { "--all", "Git.Git" })]
    [InlineData(ElevatedOperationKind.WingetUpgrade, new string[0])]
    [InlineData(ElevatedOperationKind.WingetInstall, new[] { "--all" })]
    [InlineData(ElevatedOperationKind.WingetInstall, new[] { "Git.Git", "--scope", "user" })]
    [InlineData(ElevatedOperationKind.WingetInstall, new[] { @"ARP\Machine\X64\Git_is1" })]
    [InlineData(ElevatedOperationKind.WingetInstall, new[] { "Contoso.Truncated…" })]
    [InlineData(ElevatedOperationKind.WindowsUpdateInstall, new[] { "KB5031455" })]
    [InlineData(ElevatedOperationKind.WindowsUpdateInstall, new[] { "not-a-guid" })]
    [InlineData(ElevatedOperationKind.WindowsUpdateInstall, new string[0])]
    [InlineData(ElevatedOperationKind.WingetRepairSource, new[] { "https://evil.example/source.msix" })]
    [InlineData(ElevatedOperationKind.TaskRegisterElevated, new string[0])]
    [InlineData(ElevatedOperationKind.TaskRegisterElevated, new[] { "{}" })]
    [InlineData(ElevatedOperationKind.TaskRegisterElevated, new[] { "not json" })]
    [InlineData(ElevatedOperationKind.WingetUninstall, new[] { "--all" })]
    [InlineData(ElevatedOperationKind.WingetUninstall, new string[0])]
    [InlineData(ElevatedOperationKind.WingetUninstall, new[] { "Git.Git; calc.exe" })]
    [InlineData(ElevatedOperationKind.WingetUninstall, new[] { "Git.Git", "--purge" })]
    [InlineData(ElevatedOperationKind.WingetUninstall, new[] { "-x" })]
    [InlineData(ElevatedOperationKind.WingetUninstall, new[] { @"ARP\Machine\X64\Git_is1" })]
    [InlineData(ElevatedOperationKind.WingetUninstall, new[] { "Contoso.Truncated…" })]
    public void RejectsInvalidArguments(ElevatedOperationKind kind, string[] args) =>
        Assert.Throws<ArgumentException>(() => ElevatedOperations.Validate(new ElevatedRequest(kind, args)));

    [Fact]
    public void RejectsUnknownKindsAndOversizedInput()
    {
        Assert.Throws<ArgumentException>(() => ElevatedOperations.Validate(new ElevatedRequest((ElevatedOperationKind)99, [])));
        Assert.Throws<ArgumentException>(() => ElevatedOperations.Validate(new ElevatedRequest((ElevatedOperationKind)42, ["Git.Git"])));
        Assert.Throws<ArgumentException>(() => ElevatedOperations.Validate(new ElevatedRequest(ElevatedOperationKind.WingetUpgrade, [new string('a', 129)])));
        Assert.Throws<ArgumentException>(() => ElevatedOperations.Validate(new ElevatedRequest(ElevatedOperationKind.WingetUpgrade, [.. Enumerable.Range(0, 65).Select(i => "Pkg" + i)])));
        Assert.Throws<ArgumentException>(() => ElevatedOperations.Validate(new ElevatedRequest(ElevatedOperationKind.WingetUpgrade, [null!])));
    }

    [Fact]
    public void TaskDefinitionsAreValidatedStrictly()
    {
        var ok = ElevatedOperations.Validate(new ElevatedRequest(ElevatedOperationKind.TaskRegisterElevated, [TaskDefinitionCodec.Serialize(Task())]));
        Assert.Equal("nightly", ok.Task?.Name);
        Assert.True(ok.Task?.RunElevated);

        foreach (var bad in new[]
        {
            Task(folder: @"\Microsoft\Windows\UpdateOrchestrator"),
            Task(folder: @"\"),
            Task(program: "pickle.exe"),
            Task(program: OperatingSystem.IsWindows() ? @"C:\Windows\System32\cmd.bat" : "/usr/bin/sh"),
            Task() with { Name = "../../evil" },
            Task() with { Action = new TaskActionSpec(Program, "-c \"a\"\nnext") },
            Task() with { Trigger = new TaskTriggerSpec(TaskTriggerKind.Weekly, DateTimeOffset.Now) },
            Task() with { Trigger = new TaskTriggerSpec(TaskTriggerKind.Interval, DateTimeOffset.Now, RepeatEvery: TimeSpan.FromSeconds(5)) },
        })
        {
            Assert.Throws<ArgumentException>(() => ElevatedOperations.Validate(new ElevatedRequest(ElevatedOperationKind.TaskRegisterElevated, [TaskDefinitionCodec.Serialize(bad)])));
        }

        var extraMember = TaskDefinitionCodec.Serialize(Task()).Replace("{\"name\"", "{\"runAs\":\"SYSTEM\",\"name\"", StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => ElevatedOperations.Validate(new ElevatedRequest(ElevatedOperationKind.TaskRegisterElevated, [extraMember])));
    }

    [Fact]
    public void BuildsFixedWingetCommandLines()
    {
        Assert.Equal(
            ["upgrade", "--id", "Git.Git", "--exact", "--silent", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"],
            ElevatedOperations.BuildWingetArguments(ElevatedOperationKind.WingetUpgrade, "Git.Git"));
        Assert.Equal(
            ["upgrade", "--all", "--silent", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"],
            ElevatedOperations.BuildWingetArguments(ElevatedOperationKind.WingetUpgrade, null));
        Assert.Equal(
            ["install", "--id", "7zip.7zip", "--exact", "--scope", "machine", "--silent", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"],
            ElevatedOperations.BuildWingetArguments(ElevatedOperationKind.WingetInstall, "7zip.7zip"));
        Assert.Equal(
            ["uninstall", "--id", "Git.Git", "--exact", "--silent", "--accept-source-agreements", "--disable-interactivity"],
            ElevatedOperations.BuildWingetArguments(ElevatedOperationKind.WingetUninstall, "Git.Git"));
        Assert.Throws<ArgumentException>(() => ElevatedOperations.BuildWingetArguments(ElevatedOperationKind.WingetUninstall, null));
        Assert.Throws<ArgumentException>(() => ElevatedOperations.BuildWingetArguments(ElevatedOperationKind.WingetUninstall, "--all"));
        Assert.Throws<ArgumentException>(() => ElevatedOperations.BuildWingetArguments(ElevatedOperationKind.WingetInstall, "x;y"));
        Assert.Throws<ArgumentException>(() => ElevatedOperations.BuildWingetArguments(ElevatedOperationKind.WindowsUpdateInstall, "Git.Git"));
    }

    [Fact]
    public async Task ExecutesThroughTheExecutorAndMapsFailures()
    {
        var executor = new FakeExecutor();
        var progress = new SyncProgress<string>(_ => { });
        var op = ElevatedOperations.Validate(new ElevatedRequest(ElevatedOperationKind.WingetUpgrade, ["Git.Git", "7zip.7zip"]));
        var response = await ElevatedOperations.ExecuteAsync(op, executor, progress, CancellationToken.None);
        Assert.True(response.Success);
        Assert.Equal(2, executor.Calls.Count);
        Assert.StartsWith("winget upgrade --id Git.Git --exact", executor.Calls[0], StringComparison.Ordinal);

        var wu = ElevatedOperations.Validate(new ElevatedRequest(ElevatedOperationKind.WindowsUpdateInstall, ["0A1B2C3D-4E5F-6071-8293-A4B5C6D7E8F9"]));
        await ElevatedOperations.ExecuteAsync(wu, executor, progress, CancellationToken.None);
        Assert.Equal("wu 0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9", executor.Calls[^1]);

        executor.Output = "Successfully uninstalled";
        var progressLines = new List<string>();
        var uninstall = ElevatedOperations.Validate(new ElevatedRequest(ElevatedOperationKind.WingetUninstall, ["Git.Git", "git.git", "7zip.7zip"]));
        Assert.Equal(["Git.Git", "7zip.7zip"], uninstall.Ids);
        var removed = await ElevatedOperations.ExecuteAsync(uninstall, executor, new SyncProgress<string>(progressLines.Add), CancellationToken.None);
        Assert.True(removed.Success);
        Assert.Equal("winget uninstall --id 7zip.7zip --exact --silent --accept-source-agreements --disable-interactivity", executor.Calls[^1]);
        Assert.Contains("Uninstalling Git.Git…", progressLines);
        Assert.Contains("── Git.Git ──", removed.Output, StringComparison.Ordinal);
        Assert.Contains("── 7zip.7zip ──", removed.Output, StringComparison.Ordinal);
        executor.Output = null;

        executor.Throw = new InvalidOperationException("boom");
        var failed = await ElevatedOperations.ExecuteAsync(ElevatedOperations.Validate(new ElevatedRequest(ElevatedOperationKind.WingetRepairSource, [])), executor, progress, CancellationToken.None);
        Assert.False(failed.Success);
        Assert.Contains("boom", failed.Message, StringComparison.Ordinal);
    }
}
