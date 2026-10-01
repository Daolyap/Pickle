using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Testing.Fakes;
using Pickle.Windows.Elevation;
using Pickle.Windows.Processes;
using Pickle.Windows.Storage;
using static Pickle.Abstractions.Services.DiskOperationKind;

namespace Pickle.Windows.Tests.Storage;

[SupportedOSPlatform("windows")]
public sealed class DiskConfigurationTests
{
    private static readonly IReadOnlyList<PhysicalDisk> Online = [SampleDisks.System, SampleDisks.Data, SampleDisks.Blank with { IsOffline = false }];

    /// <summary>Answers the executor's two kinds of PowerShell calls: reading the layout, and running one disk script (recording its environment).</summary>
    private sealed class DiskRunner(IReadOnlyList<PhysicalDisk> layout) : IProcessRunner
    {
        public List<IReadOnlyDictionary<string, string?>> Scripts { get; } = [];

        public List<string> ScriptText { get; } = [];

        public Func<int, ProcessResult>? Reply { get; set; }

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, Action<string>? onSegment, TimeSpan timeout, CancellationToken cancellationToken, IReadOnlyDictionary<string, string?>? environment = null)
        {
            var script = Encoding.Unicode.GetString(Convert.FromBase64String(arguments[^1]));
            if (script == DiskLayoutService.Script)
            {
                return Task.FromResult(new ProcessResult(0, LayoutJson(layout)));
            }

            Scripts.Add(environment!);
            ScriptText.Add(script);
            return Task.FromResult(Reply?.Invoke(Scripts.Count - 1) ?? new ProcessResult(0, "OK"));
        }
    }

    private static string LayoutJson(IReadOnlyList<PhysicalDisk> disks) => JsonSerializer.Serialize(disks.Select(d => new
    {
        d.Number,
        d.Name,
        d.PartitionStyle,
        d.Size,
        d.AllocatedSize,
        d.Status,
        d.IsOffline,
        d.IsReadOnly,
        d.IsBoot,
        d.IsSystem,
        d.BusType,
        Partitions = d.Partitions.Select(p => new { p.DiskNumber, p.PartitionNumber, DriveLetter = p.DriveLetter?.ToString(), p.Size, p.Type, p.IsBoot, p.IsSystem, p.FileSystem, Label = p.Label ?? string.Empty, Free = p.FreeBytes }),
    }));

    private static WindowsElevatedExecutor Executor(DiskRunner runner) => new(new ListLogger(), runner);

    private static List<StorageStepResult> Steps(ElevatedResponse response) => JsonSerializer.Deserialize<List<StorageStepResult>>(response.Output!, PickleJson.Compact)!;

    // ───────────── the allowlist ─────────────

    [Fact]
    public void StorageOperationsTakeOneValidatedJsonArray()
    {
        var json = DiskOperationCodec.Serialize([new DiskOperation(CleanDisk, 1), new DiskOperation(NewVolume, 2) { FileSystem = "NTFS" }]);

        var operation = ElevatedOperations.Validate(new ElevatedRequest(ElevatedOperationKind.StorageOperations, [json]));

        Assert.Equal([CleanDisk, NewVolume], operation.Disk!.Select(o => o.Kind));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("[{\"kind\":\"cleanDisk\",\"diskNumber\":1,\"extra\":1}]")]
    [InlineData("[{\"kind\":\"formatVolume\",\"diskNumber\":1,\"partitionNumber\":1}]")]
    public void BadStorageJsonNeverReachesTheExecutor(string json) =>
        Assert.Throws<ArgumentException>(() => ElevatedOperations.Validate(new ElevatedRequest(ElevatedOperationKind.StorageOperations, [json])));

    [Fact]
    public void StorageOperationsTakeExactlyOneArgument()
    {
        var one = DiskOperationCodec.Serialize([new DiskOperation(CleanDisk, 1)]);

        Assert.Throws<ArgumentException>(() => ElevatedOperations.Validate(new ElevatedRequest(ElevatedOperationKind.StorageOperations, [])));
        Assert.Throws<ArgumentException>(() => ElevatedOperations.Validate(new ElevatedRequest(ElevatedOperationKind.StorageOperations, [one, one])));
    }

    [Fact]
    public async Task ValidatedStorageOperationsReachTheExecutorWithALongTimeout()
    {
        var executor = new FakeExecutor();
        var json = DiskOperationCodec.Serialize([new DiskOperation(InitializeDisk, 2) { Option = "GPT" }]);

        await ElevatedOperations.ExecuteAsync(ElevatedOperations.Validate(new ElevatedRequest(ElevatedOperationKind.StorageOperations, [json])), executor, new Progress<string>(), CancellationToken.None);

        Assert.Equal(["storage Initialize disk 2 as GPT"], executor.Calls);
        Assert.True(ElevatedOperations.TimeoutFor(ElevatedOperationKind.StorageOperations) >= TimeSpan.FromHours(1));
    }

    // ───────────── the elevated side ─────────────

    [Fact]
    public async Task EachOperationRunsTheFixedScriptWithItsValuesInTheEnvironmentOnly()
    {
        var runner = new DiskRunner(Online);
        var label = "Dataé $(calc) `n 'x'";
        var operations = new[]
        {
            new DiskOperation(InitializeDisk, 2) { Option = "gpt" },
            new DiskOperation(NewVolume, 1) { SizeBytes = 100L << 30, FileSystem = "ntfs", Label = "Data", DriveLetter = 'f' },
            new DiskOperation(SetLabel, 1) { PartitionNumber = 1, Label = label.Replace("`n", string.Empty, StringComparison.Ordinal) },
        };
        runner.Reply = _ => new ProcessResult(0, "OK");

        var response = await Executor(runner).RunStorageOperationsAsync(operations, new Progress<string>(), CancellationToken.None);

        Assert.True(response.Success, response.Message);
        Assert.Equal("3 disk changes applied.", response.Message);
        Assert.Equal([true, true, true], Steps(response).Select(s => s.Success));
        Assert.All(runner.ScriptText, text => Assert.Equal(WindowsElevatedExecutor.StorageScript, text));
        Assert.Equal(("InitializeDisk", "2", "GPT"), (runner.Scripts[0]["PICKLE_DISK_OP"], runner.Scripts[0]["PICKLE_DISK"], runner.Scripts[0]["PICKLE_OPTION"]));
        Assert.Equal(("NewVolume", "1", (100L << 30).ToString(), "NTFS", "Data", "F"), (runner.Scripts[1]["PICKLE_DISK_OP"], runner.Scripts[1]["PICKLE_DISK"], runner.Scripts[1]["PICKLE_SIZE"], runner.Scripts[1]["PICKLE_FS"], runner.Scripts[1]["PICKLE_LABEL"], runner.Scripts[1]["PICKLE_LETTER"]));
        Assert.Equal("1", runner.Scripts[2]["PICKLE_PARTITION"]);
    }

    [Fact]
    public async Task ValuesThatLookLikeScriptAreOnlyEverEnvironmentVariables()
    {
        var runner = new DiskRunner(Online);

        await Executor(runner).RunStorageOperationsAsync([new DiskOperation(SetLabel, 1) { PartitionNumber = 1, Label = "$(calc) (x)" }], new Progress<string>(), CancellationToken.None);

        Assert.Equal("$(calc) (x)", runner.Scripts.Single()["PICKLE_LABEL"]);
        Assert.DoesNotContain("calc", runner.ScriptText.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOperationThatBreaksTheRulesIsRefusedWithoutRunningAnything()
    {
        var runner = new DiskRunner(Online);

        var response = await Executor(runner).RunStorageOperationsAsync([new DiskOperation(CleanDisk, 0)], new Progress<string>(), CancellationToken.None);

        Assert.False(response.Success);
        Assert.Contains("Windows or the boot files", response.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Scripts);
        Assert.False(Steps(response).Single().Success);
    }

    [Fact]
    public async Task TheLiveDisksDecideNotWhatTheCallerSaw()
    {
        // The panel may have planned a format of E: while the disk has since become read-only.
        var runner = new DiskRunner([SampleDisks.System, SampleDisks.Data with { IsReadOnly = true }]);

        var response = await Executor(runner).RunStorageOperationsAsync([new DiskOperation(FormatVolume, 1) { PartitionNumber = 1, FileSystem = "NTFS" }], new Progress<string>(), CancellationToken.None);

        Assert.False(response.Success);
        Assert.Contains("read-only", response.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Scripts);
    }

    [Fact]
    public async Task ItStopsAtTheFirstFailureAndReportsHowFarItGot()
    {
        var runner = new DiskRunner(Online) { Reply = i => i == 1 ? new ProcessResult(1, "Format-Volume : The volume is in use.\nmore") : new ProcessResult(0, "OK") };
        var operations = new[]
        {
            new DiskOperation(SetLabel, 1) { PartitionNumber = 1, Label = "One" },
            new DiskOperation(FormatVolume, 1) { PartitionNumber = 1, FileSystem = "NTFS" },
            new DiskOperation(DeletePartition, 1) { PartitionNumber = 1 },
        };

        var response = await Executor(runner).RunStorageOperationsAsync(operations, new Progress<string>(), CancellationToken.None);

        Assert.False(response.Success);
        Assert.Equal("1 of 3 disk changes applied; the next one failed: Format-Volume : The volume is in use.", response.Message);
        Assert.Equal(2, runner.Scripts.Count);
        Assert.Equal([true, false], Steps(response).Select(s => s.Success));
    }

    [Fact]
    public async Task ATimedOutScriptIsAFailure()
    {
        var runner = new DiskRunner(Online) { Reply = _ => new ProcessResult(-1, string.Empty, TimedOut: true) };

        var response = await Executor(runner).RunStorageOperationsAsync([new DiskOperation(OptimizeVolume, 1) { PartitionNumber = 1, Option = "defrag" }], new Progress<string>(), CancellationToken.None);

        Assert.False(response.Success);
        Assert.Contains("timed out", response.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AScriptsOutputIsKeptAsTheStepMessage()
    {
        var runner = new DiskRunner(Online) { Reply = _ => new ProcessResult(0, "Result: NoErrorsFound\nOK") };

        var response = await Executor(runner).RunStorageOperationsAsync([new DiskOperation(CheckVolume, 1) { PartitionNumber = 1, Option = "scan" }], new Progress<string>(), CancellationToken.None);

        Assert.Equal("Result: NoErrorsFound", Steps(response).Single().Message);
    }

    [Fact]
    public void TheScriptHasABranchForEveryKindAndNoWayToRunAnythingElse()
    {
        foreach (var kind in Enum.GetValues<DiskOperationKind>())
        {
            Assert.Contains($"'{kind}'", WindowsElevatedExecutor.StorageScript, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("Invoke-Expression", WindowsElevatedExecutor.StorageScript, StringComparison.Ordinal);
        Assert.DoesNotContain("iex ", WindowsElevatedExecutor.StorageScript, StringComparison.Ordinal);
    }

    // ───────────── the service the panel uses ─────────────

    private sealed class FixedLayout(IReadOnlyList<PhysicalDisk> disks) : IDiskLayoutService
    {
        public Task<IReadOnlyList<PhysicalDisk>> GetDisksAsync(CancellationToken cancellationToken = default) => Task.FromResult(disks);
    }

    private sealed class RangeShell(string? output) : IPickleShell
    {
        public Dictionary<string, object?>? Parameters { get; private set; }

        public string CurrentDirectory => "/";

        public bool IsInteractive => false;

        public bool IsBusy => false;

        public Task<ShellResult> InvokeAsync(string script, IReadOnlyDictionary<string, object?>? parameters = null, ShellTarget target = ShellTarget.Main, CancellationToken cancellationToken = default)
        {
            Parameters = parameters?.ToDictionary(p => p.Key, p => p.Value);
            return Task.FromResult(output is null ? ShellResult.Empty : new ShellResult([new System.Management.Automation.PSObject(output)], []));
        }

        public void InsertText(string text)
        {
        }

        public void ReplaceInput(string text)
        {
        }

        public void SubmitCommand(string commandLine)
        {
        }

        public void SetLocation(string path)
        {
        }

        public void WriteLine(string text)
        {
        }

        public void OpenPanelWhenIdle(PanelDescriptor panel, string? argument = null, string? currentInput = null)
        {
        }
    }

    private static DiskConfigurationService Service(FakeElevationBroker broker, IPickleShell? shell = null) => new(new FixedLayout(Online), broker, () => shell);

    [Fact]
    public async Task ApplySendsOneRequestAndMapsTheStepsBack()
    {
        var operations = new[] { new DiskOperation(SetLabel, 1) { PartitionNumber = 1, Label = "One" }, new DiskOperation(CleanDisk, 1) };
        var steps = JsonSerializer.Serialize(new[] { new StorageStepResult(true, "Done."), new StorageStepResult(false, "In use.") }, PickleJson.Compact);
        var broker = new FakeElevationBroker { Respond = _ => new ElevatedResponse(false, "1 of 2 applied", 1, steps) };

        var results = await Service(broker).ApplyAsync(operations);

        var request = Assert.Single(broker.Requests);
        Assert.Equal(ElevatedOperationKind.StorageOperations, request.Kind);
        Assert.Equal(operations, DiskOperationCodec.Deserialize(Assert.Single(request.Arguments)));
        Assert.Equal([(operations[0], true, "Done."), (operations[1], false, "In use.")], results.Select(r => (r.Operation, r.Success, r.Message)));
    }

    [Fact]
    public async Task WithoutStepDetailTheOverallOutcomeStandsInForTheFirstOperation()
    {
        var broker = new FakeElevationBroker { Respond = _ => new ElevatedResponse(false, "The elevated helper stopped: pipe closed", 1) };

        var results = await Service(broker).ApplyAsync([new DiskOperation(CleanDisk, 1)]);

        Assert.Equal([(false, "The elevated helper stopped: pipe closed")], results.Select(r => (r.Success, r.Message)));
    }

    [Fact]
    public async Task ADeclinedUacPromptSurfacesAsCancellationAndBadOperationsNeverGoOut()
    {
        var declined = new FakeElevationBroker { DeclineUac = true };
        var broker = new FakeElevationBroker();

        await Assert.ThrowsAsync<OperationCanceledException>(() => Service(declined).ApplyAsync([new DiskOperation(CleanDisk, 1)]));
        await Assert.ThrowsAsync<ArgumentException>(() => Service(broker).ApplyAsync([new DiskOperation(FormatVolume, 1)]));
        Assert.Empty(broker.Requests);
    }

    [Fact]
    public void ElevationIsNeededUnlessPickleIsAlreadyElevated()
    {
        Assert.True(Service(new FakeElevationBroker()).NeedsElevation);
        Assert.False(Service(new FakeElevationBroker { IsElevated = true }).NeedsElevation);
        Assert.False(Service(new FakeElevationBroker { IsSupported = false }).IsSupported);
    }

    [Fact]
    public async Task TheResizeRangeComesFromGetPartitionSupportedSize()
    {
        var shell = new RangeShell("1048576 214748364800");

        var range = await Service(new FakeElevationBroker(), shell).GetResizeRangeAsync(1, 1);

        Assert.Equal(new DiskResizeRange(1048576, 214748364800), range);
        Assert.Equal((1, 1), ((int)shell.Parameters!["d"]!, (int)shell.Parameters["p"]!));
        Assert.Null(await Service(new FakeElevationBroker(), new RangeShell(null)).GetResizeRangeAsync(1, 1));
        Assert.Null(DiskConfigurationService.ParseRange("5 2"));
        Assert.Null(DiskConfigurationService.ParseRange("a b"));
    }

    [Fact]
    public async Task ReadingDisksNeedsNoElevation()
    {
        var broker = new FakeElevationBroker();

        var disks = await Service(broker).GetDisksAsync();

        Assert.Equal(3, disks.Count);
        Assert.Empty(broker.Requests);
    }
}
