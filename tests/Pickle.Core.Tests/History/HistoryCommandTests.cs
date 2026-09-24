using System.Management.Automation;
using Pickle.Abstractions;
using Pickle.Testing;

namespace Pickle.Core.Tests.History;

public class HistoryCommandTests
{
    [Fact]
    public void ListShowsTheLastEntries()
    {
        using var t = Seeded();
        t.Runtime.Engine.ExecuteInteractive("pk history list 2");
        var screen = t.Terminal.GetScreenText();
        Assert.DoesNotContain("git clone", screen, StringComparison.Ordinal);
        Assert.Contains("dotnet build", screen, StringComparison.Ordinal);
        Assert.Contains("git status", screen, StringComparison.Ordinal);
    }

    [Fact]
    public void ListAcceptsACountFlag()
    {
        using var t = Seeded();
        t.Runtime.Engine.ExecuteInteractive("pk history list --count 1");
        var screen = t.Terminal.GetScreenText();
        Assert.Contains("git status", screen, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet build", screen, StringComparison.Ordinal);
    }

    [Fact]
    public void ListAcceptsQuotedDashN()
    {
        using var t = Seeded();
        t.Runtime.Engine.ExecuteInteractive("pk history list '-n' 1");
        var screen = t.Terminal.GetScreenText();
        Assert.Contains("git status", screen, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet build", screen, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchIsFuzzy()
    {
        using var t = Seeded();
        t.Runtime.Engine.ExecuteInteractive("pk history search gcl");
        var screen = t.Terminal.GetScreenText();
        Assert.Contains("git clone https://example.com/r.git", screen, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet build", screen, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatsReturnsAnObject()
    {
        using var t = Seeded();
        var result = await t.Runtime.Shell.InvokeAsync("pk history stats");
        var stats = Assert.Single(result.Output);
        Assert.Equal(3, (int)stats.Properties["Entries"].Value);
        Assert.Equal(66.7, (double)stats.Properties["SuccessRate"].Value);
        Assert.Contains("git (2)", (string[])stats.Properties["TopCommands"].Value);
    }

    [Fact]
    public async Task ClearRequiresForceWhenNotInteractive()
    {
        using var t = Seeded();
        t.Terminal.IsInteractive = false;
        await t.Runtime.Shell.InvokeAsync("pk history clear");
        Assert.Equal(3, t.Runtime.History.Entries.Count);

        await t.Runtime.Shell.InvokeAsync("pk history clear --force");
        Assert.Empty(t.Runtime.History.Entries);
    }

    [Fact]
    public async Task UnknownSubcommandFails()
    {
        using var t = Seeded();
        var result = await t.Runtime.Shell.InvokeAsync("pk history frobnicate");
        Assert.True(result.HadErrors);
    }

    [Fact]
    public async Task GetPickleHistoryFiltersAndRanks()
    {
        using var t = Seeded();
        Assert.Equal(["git clone https://example.com/r.git", "dotnet build", "git status"], t.Run("(Get-PickleHistory).CommandLine"));
        Assert.Equal(["git status"], t.Run("(Get-PickleHistory -Count 1).CommandLine"));
        Assert.Equal(["git status", "git clone https://example.com/r.git"], t.Run("(Get-PickleHistory git).CommandLine"));
        var inApp = await t.Runtime.Shell.InvokeAsync(
            "param($d) (Get-PickleHistory -Directory $d).CommandLine",
            new Dictionary<string, object?> { ["d"] = AppDir });
        Assert.Equal(["dotnet build"], inApp.Output.Select(o => o.ToString()));
        Assert.Equal(["git status"], t.Run("(Get-PickleHistory -Query stat -Count 1).CommandLine"));
        Assert.IsType<HistoryEntry>((await t.Runtime.Shell.InvokeAsync("Get-PickleHistory -Count 1")).Output[0].BaseObject);
    }

    private static readonly string WorkDir = Path.Combine(Path.GetTempPath(), "pickle-w2-work");
    private static readonly string AppDir = Path.Combine(WorkDir, "app");

    private static TestPickle Seeded()
    {
        var t = TestPickle.Create(width: 120, height: 30, start: true);
        var history = t.Runtime.History;
        Add(history, "git clone https://example.com/r.git", WorkDir, true);
        Add(history, "dotnet build", AppDir, false);
        Add(history, "git status", WorkDir, true);
        return t;
    }

    private static void Add(IHistoryStore history, string command, string cwd, bool success)
    {
        history.Add(new HistoryEntry(command, DateTimeOffset.Now, cwd));
        history.CompleteLast(success, 10);
    }
}
