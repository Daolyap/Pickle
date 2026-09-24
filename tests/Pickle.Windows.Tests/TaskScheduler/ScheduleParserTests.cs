using Pickle.Abstractions.Services;
using Pickle.Windows.TaskScheduler;

namespace Pickle.Windows.Tests.TaskScheduler;

public class ScheduleParserTests
{
    // Wednesday 2026-09-23 10:15:42 UTC.
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 10, 15, 42, TimeSpan.Zero);

    private static TaskTriggerSpec Parse(string text) => ScheduleParser.Parse(text, Now, TimeZoneInfo.Utc);

    private static DateTimeOffset At(int month, int day, int hour, int minute) => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("every 30m", 30)]
    [InlineData("every 2h", 120)]
    [InlineData("EVERY  90 minutes", 90)]
    [InlineData("every 1 hour", 60)]
    public void ParsesIntervals(string text, int minutes)
    {
        var spec = Parse(text);
        Assert.Equal(TaskTriggerKind.Interval, spec.Kind);
        Assert.Equal(TimeSpan.FromMinutes(minutes), spec.RepeatEvery);
        Assert.Equal(At(9, 23, 10, 15), spec.Start);
    }

    [Fact]
    public void EveryNDaysIsADailyTrigger()
    {
        var spec = Parse("every 3d");
        Assert.Equal(TaskTriggerKind.Daily, spec.Kind);
        Assert.Equal(3, spec.DaysInterval);
        Assert.Equal(1, Parse("every 24h").DaysInterval);
    }

    [Fact]
    public void ParsesDailyAsNextOccurrence()
    {
        Assert.Equal(new TaskTriggerSpec(TaskTriggerKind.Daily, At(9, 23, 18, 30)), Parse("daily 18:30"));
        Assert.Equal(new TaskTriggerSpec(TaskTriggerKind.Daily, At(9, 24, 9, 0)), Parse("daily 09:00"));
        Assert.Equal(At(9, 24, 9, 5), Parse("daily 9:05").Start);
    }

    [Fact]
    public void ParsesWeekly()
    {
        var spec = Parse("weekly mon,fri 18:30");
        Assert.Equal(TaskTriggerKind.Weekly, spec.Kind);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Friday], spec.DaysOfWeek);
        Assert.Equal(At(9, 25, 18, 30), spec.Start);

        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday], Parse("weekly mon-fri 07:00").DaysOfWeek);
        Assert.Equal([DayOfWeek.Sunday, DayOfWeek.Saturday], Parse("weekly weekends 10:00").DaysOfWeek);
        Assert.Equal([DayOfWeek.Sunday, DayOfWeek.Wednesday], Parse("weekly Wednesday, sun 12:00").DaysOfWeek);
        Assert.Equal(At(9, 23, 12, 0), Parse("weekly wed 12:00").Start);
    }

    [Fact]
    public void ParsesMonthly()
    {
        var spec = Parse("monthly 1,15 08:00");
        Assert.Equal(TaskTriggerKind.Monthly, spec.Kind);
        Assert.Equal([1, 15], spec.DaysOfMonth);
        Assert.Equal(At(9, 24, 8, 0), spec.Start);
    }

    [Theory]
    [InlineData("at logon", TaskTriggerKind.AtLogon)]
    [InlineData("At Login", TaskTriggerKind.AtLogon)]
    [InlineData("at startup", TaskTriggerKind.AtStartup)]
    [InlineData("at boot", TaskTriggerKind.AtStartup)]
    [InlineData("on idle", TaskTriggerKind.OnIdle)]
    public void ParsesEventTriggers(string text, TaskTriggerKind kind)
    {
        var spec = Parse(text);
        Assert.Equal(kind, spec.Kind);
        Assert.Null(spec.Start);
    }

    [Fact]
    public void ParsesOnceAndIn()
    {
        Assert.Equal(new TaskTriggerSpec(TaskTriggerKind.Once, At(10, 1, 12, 0)), Parse("once 2026-10-01 12:00"));
        Assert.Equal(new TaskTriggerSpec(TaskTriggerKind.Once, At(9, 23, 23, 0)), Parse("once 23:00"));
        Assert.Equal(new TaskTriggerSpec(TaskTriggerKind.Once, Now.AddMinutes(10)), Parse("in 10m"));
        Assert.Equal(Now.AddHours(2), Parse("in 2 hours").Start);
    }

    [Fact]
    public void UsesTheGivenTimeZone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("plus2", TimeSpan.FromHours(2), "plus2", "plus2");
        var spec = ScheduleParser.Parse("daily 09:00", Now, zone);
        Assert.Equal(new DateTimeOffset(2026, 9, 24, 9, 0, 0, TimeSpan.FromHours(2)), spec.Start);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("sometimes", "Unrecognized schedule")]
    [InlineData("every 0m", "positive")]
    [InlineData("every 5s", "unknown unit")]
    [InlineData("every 50000m", "between 1 minute and 31 days")]
    [InlineData("every 400d", "between 1 and 365")]
    [InlineData("daily 25:00", "invalid time")]
    [InlineData("daily 9", "invalid time")]
    [InlineData("daily", "add a time")]
    [InlineData("weekly fry 10:00", "unknown day 'fry'")]
    [InlineData("weekly mon", "add a time")]
    [InlineData("monthly 0,40 08:00", "1-31")]
    [InlineData("once 2020-01-01 12:00", "in the past")]
    [InlineData("once tomorrow", "yyyy-MM-dd")]
    [InlineData("in 0m", "positive")]
    public void RejectsBadSchedulesWithClearMessages(string text, string expected)
    {
        var ex = Assert.Throws<FormatException>(() => Parse(text));
        Assert.Contains(expected, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DescribesSpecs()
    {
        Assert.Equal("every 30m", ScheduleParser.Describe(Parse("every 30m")));
        Assert.Equal("every 2h", ScheduleParser.Describe(Parse("every 2h")));
        Assert.Equal("daily at 18:30", ScheduleParser.Describe(Parse("daily 18:30")));
        Assert.Equal("weekly on Mon,Fri at 18:30", ScheduleParser.Describe(Parse("weekly mon,fri 18:30")));
        Assert.Equal("at logon", ScheduleParser.Describe(Parse("at logon")));
    }
}
