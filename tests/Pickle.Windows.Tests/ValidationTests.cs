using Pickle.Abstractions.Services;
using Pickle.Windows.Commands;
using Pickle.Windows.TaskScheduler;
using Pickle.Windows.WindowsUpdate;

namespace Pickle.Windows.Tests;

public class ValidationTests
{
    [Theory]
    [InlineData("Git.Git", true)]
    [InlineData("Microsoft.VCRedist.2015+.x64", true)]
    [InlineData("7zip.7zip", true)]
    [InlineData("a", true)]
    [InlineData("", false)]
    [InlineData(".hidden", false)]
    [InlineData("-x", false)]
    [InlineData("Git Git", false)]
    [InlineData("Git.Git;calc", false)]
    [InlineData("Git.Git&calc", false)]
    [InlineData("Git`Git", false)]
    [InlineData(@"ARP\Machine\X64\Git_is1", false)]
    [InlineData("Contoso.Trunc…", false)]
    [InlineData("Ｇit.Git", false)]
    public void WingetIds(string id, bool valid) => Assert.Equal(valid, WindowsIds.IsValidWingetId(id));

    [Fact]
    public void WingetIdLengthLimit()
    {
        Assert.True(WindowsIds.IsValidWingetId(new string('a', 128)));
        Assert.False(WindowsIds.IsValidWingetId(new string('a', 129)));
        Assert.Contains("truncated", Assert.Throws<ArgumentException>(() => WindowsIds.RequireWingetId("Contoso.Trunc…")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateIdsAndKbs()
    {
        Assert.True(WindowsIds.TryNormalizeUpdateId("0A1B2C3D-4E5F-6071-8293-A4B5C6D7E8F9", out var id));
        Assert.Equal("0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9", id);
        Assert.True(WindowsIds.TryNormalizeUpdateId("{0A1B2C3D-4E5F-6071-8293-A4B5C6D7E8F9}", out _));
        Assert.False(WindowsIds.TryNormalizeUpdateId("0A1B2C3D4E5F60718293A4B5C6D7E8F9", out _));
        Assert.False(WindowsIds.TryNormalizeUpdateId("x' or 1=1", out _));

        Assert.True(WindowsIds.TryNormalizeKb("kb5031455", out var kb));
        Assert.Equal("KB5031455", kb);
        Assert.True(WindowsIds.TryNormalizeKb("5031455", out _));
        Assert.False(WindowsIds.TryNormalizeKb("KB12", out _));
        Assert.False(WindowsIds.TryNormalizeKb("KB5031455'", out _));
    }

    [Theory]
    [InlineData(@"\", true)]
    [InlineData(@"\Pickle", true)]
    [InlineData(@"\Pickle\Test", true)]
    [InlineData(@"\Pickle\", true)]
    [InlineData(@"Pickle", false)]
    [InlineData(@"\\Pickle", false)]
    [InlineData(@"\Pickle\..\Microsoft", false)]
    [InlineData(@"\Pick|le", false)]
    public void TaskFolders(string folder, bool valid) => Assert.Equal(valid, WindowsIds.IsValidTaskFolder(folder));

    [Fact]
    public void PickleFolderDetection()
    {
        Assert.True(WindowsIds.IsPickleTaskFolder(@"\Pickle"));
        Assert.True(WindowsIds.IsPickleTaskFolder(@"\pickle\Test"));
        Assert.False(WindowsIds.IsPickleTaskFolder(@"\PickleEvil"));
        Assert.False(WindowsIds.IsPickleTaskFolder(@"\Microsoft\Windows"));
    }

    [Theory]
    [InlineData("simple", "simple")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("", "\"\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\path with space\", "\"C:\\path with space\\\\\"")]
    [InlineData(@"Get-Date | Out-File C:\x.txt", "\"Get-Date | Out-File C:\\x.txt\"")]
    public void QuotesWindowsArguments(string input, string expected) => Assert.Equal(expected, WindowsCommandLine.Quote(input));

    [Fact]
    public void ResolvesTaskPaths()
    {
        Assert.Equal(@"\Pickle\nightly", ScheduleCommand.ResolvePath("nightly"));
        Assert.Equal(@"\Other\Task", ScheduleCommand.ResolvePath(@"\Other\Task"));
        Assert.Throws<ArgumentException>(() => ScheduleCommand.ResolvePath("a/b|c"));
        Assert.Throws<ArgumentException>(() => ScheduleCommand.ResolvePath(" "));
        Assert.Equal("Get-Date-20260923-101542", ScheduleCommand.DefaultName("Get-Date | Out-File x", new DateTimeOffset(2026, 9, 23, 10, 15, 42, TimeSpan.Zero)));
        Assert.StartsWith("task-", ScheduleCommand.DefaultName("& 'x'", DateTimeOffset.Now), StringComparison.Ordinal);
    }

    [Fact]
    public void TaskDefinitionsRoundTrip()
    {
        var definition = Elevation.ElevatedOperationsTests.Task() with
        {
            Trigger = new TaskTriggerSpec(TaskTriggerKind.Weekly, DateTimeOffset.Now, DaysOfWeek: [DayOfWeek.Monday], RepeatEvery: TimeSpan.FromMinutes(30)),
        };
        var json = TaskDefinitionCodec.Serialize(definition);
        var back = TaskDefinitionCodec.Deserialize(json);
        Assert.Equal(definition.Name, back.Name);
        Assert.Equal(definition.Action, back.Action);
        Assert.Equal([DayOfWeek.Monday], back.Trigger.DaysOfWeek);
        Assert.Equal(TimeSpan.FromMinutes(30), back.Trigger.RepeatEvery);
        TaskDefinitionCodec.Validate(back, elevated: true);
    }

    [Fact]
    public void WuaCriteriaAndMapping()
    {
        Assert.Equal("IsInstalled=0 and IsHidden=0 and Type='Software' and BrowseOnly=0", WuaText.BuildCriteria(new WindowsUpdateQuery(IncludeDrivers: false)));
        Assert.Equal("IsInstalled=0 and IsHidden=0", WuaText.BuildCriteria(new WindowsUpdateQuery(IncludeDrivers: true, IncludeOptional: true)));
        Assert.Equal("IsInstalled=0 and BrowseOnly=0", WuaText.BuildCriteria(new WindowsUpdateQuery(IncludeHidden: true)));

        var info = WuaText.ToInfo(new WuaRawUpdate(
            "0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9", "2026-09 Cumulative Update for Windows 11 (KB5031455)", ["5031455"], ["Security Updates"],
            123_456_789m, false, false, false, 1, "Critical", "desc", new DateTime(2026, 9, 9), 2));
        Assert.Equal("KB5031455", info.KbArticle);
        Assert.Equal(123_456_789L, info.SizeBytes);
        Assert.False(info.IsDriver);
        Assert.True(info.RebootMayBeRequired);
        Assert.Equal("Critical", info.Severity);
        Assert.Equal(UpdateGroups.SecurityCritical, UpdateGroups.Of(info));

        var driver = WuaText.ToInfo(new WuaRawUpdate("id", "Intel - Display - 31.0.101", [], ["Drivers"], null, false, false, true, 2, " ", null, null, 0));
        Assert.True(driver.IsDriver);
        Assert.True(driver.IsOptional);
        Assert.Null(driver.Severity);
        Assert.Null(driver.ReleaseDate);
        Assert.False(WuaText.Matches(driver, new WindowsUpdateQuery(IncludeDrivers: true, IncludeOptional: false)));
        Assert.Equal(UpdateGroups.Optional, UpdateGroups.Of(driver));
    }

    [Fact]
    public void WuaTextHelpers()
    {
        Assert.Equal("KB5031455", WuaText.ExtractKb("Security Update (kb5031455)"));
        Assert.Null(WuaText.ExtractKb("Defender definitions"));
        Assert.Equal("Installation", WuaText.OperationName(1));
        Assert.Equal("Succeeded with errors", WuaText.ResultName(3));
        Assert.Contains("internet connection", WuaText.Describe(unchecked((int)0x8024402C)), StringComparison.Ordinal);
        Assert.Contains("0x80070422", WuaText.Describe(unchecked((int)0x80070422)), StringComparison.Ordinal);
        Assert.Equal("Windows Update error 0x80001234.", WuaText.Describe(unchecked((int)0x80001234)));
    }
}
