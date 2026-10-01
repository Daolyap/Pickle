using System.Management.Automation;
using Pickle.Abstractions.Services;
using Pickle.Windows.Logs;

namespace Pickle.Windows.Tests.Logs;

public class WindowsEventLogSourceTests
{
    private static PSObject Row(DateTime time, int level, string provider, string message, int id = 7036)
    {
        var row = new PSObject();
        row.Properties.Add(new PSNoteProperty("Time", time));
        row.Properties.Add(new PSNoteProperty("Level", level));
        row.Properties.Add(new PSNoteProperty("Provider", provider));
        row.Properties.Add(new PSNoteProperty("Message", message));
        row.Properties.Add(new PSNoteProperty("Id", id));
        return row;
    }

    [Theory]
    [InlineData(1, LogSeverity.Critical)]
    [InlineData(2, LogSeverity.Error)]
    [InlineData(3, LogSeverity.Warning)]
    [InlineData(4, LogSeverity.Info)]
    [InlineData(5, LogSeverity.Debug)]
    [InlineData(0, LogSeverity.Info)]
    public void LevelsMapToSeverities(int level, LogSeverity expected) => Assert.Equal(expected, WindowsEventLogSource.FromLevel(level));

    [Fact]
    public void MapsRowsAndFlattensMessages()
    {
        var entry = WindowsEventLogSource.Map(Row(new DateTime(2025, 10, 1, 12, 0, 0, DateTimeKind.Local), 2, "Service Control Manager", "The service\r\nterminated unexpectedly."))!;

        Assert.Equal(("Service Control Manager", LogSeverity.Error, "The service terminated unexpectedly."), (entry.Source, entry.Severity, entry.Message));
        Assert.Equal("7036", entry.Fields["Event id"]);
    }

    [Theory]
    [InlineData(LogSeverity.Critical, new[] { 1 })]
    [InlineData(LogSeverity.Error, new[] { 1, 2 })]
    [InlineData(LogSeverity.Warning, new[] { 1, 2, 3 })]
    public void SeverityBecomesALevelFilter(LogSeverity severity, int[] levels) => Assert.Equal(levels, WindowsEventLogSource.Levels(severity));

    [Fact]
    public void NoFilterForInfoAndBelow() => Assert.Null(WindowsEventLogSource.Levels(LogSeverity.Info));
}
