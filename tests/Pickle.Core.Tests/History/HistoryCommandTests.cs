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
    public void SearchIsFuzzy()
    {
        using var t = Seeded();
        t.Runtime.Engine.ExecuteInteractive("pk history search gcl");
        var screen = t.Terminal.GetScreenText();
        Assert.Contains("git clone https://example.com/r.git", screen, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet build", screen, StringComparison.Ordinal);
    }

    [Fact]
    public void StatsReturnsAnObject()
    {
        using var t = Seeded();
        var result = t.Runtime.Shell.InvokeAsync("pk history stats").GetAwaiter().GetResult();
        var stats = Assert.Single(result.Output);
        Assert.Equal(3, (int)stats.Properties["Entries"].Value);
        Assert.Equal(66.7, (double)stats.Properties["SuccessRate"].Value);
        Assert.Contains("git (2)", (string[])stats.Properties["TopCommands"].Value);
    }

    [Fact]
    public void ClearRequiresForceWhenNotInteractive()
    {
        using var t = Seeded();
        t.Terminal.IsInteractive = false;
        t.Runtime.Shell.InvokeAsync("pk history clear").GetAwaiter().GetResult();
        Assert.Equal(3, t.Runtime.History.Entries.Count);

        t.Runtime.Shell.InvokeAsync("pk history clear --force").GetAwaiter().GetResult();
        Assert.Empty(t.Runtime.History.Entries);
    }

    [Fact]
    public void UnknownSubcommandFails()
    {
        using var t = Seeded();
        var result = t.Runtime.Shell.InvokeAsync("pk history frobnicate").GetAwaiter().GetResult();
        Assert.True(result.HadErrors);
    }

    [Fact]
    public void GetPickleHistoryFiltersAndRanks()
    {
        using var t = Seeded();
        Assert.Equal(["git clone https://example.com/r.git", "dotnet build", "git status"], t.Run("(Get-PickleHistory).CommandLine"));
        Assert.Equal(["git status"], t.Run("(Get-PickleHistory -Count 1).CommandLine"));
        Assert.Equal(["git status", "git clone https://example.com/r.git"], t.Run("(Get-PickleHistory git).CommandLine"));
        Assert.Equal(["dotnet build"], t.Run("(Get-PickleHistory -Directory '/work/app').CommandLine"));
        Assert.Equal(["git status"], t.Run("(Get-PickleHistory -Query stat -Count 1).CommandLine"));
        Assert.IsType<HistoryEntry>(t.Runtime.Shell.InvokeAsync("Get-PickleHistory -Count 1").GetAwaiter().GetResult().Output[0].BaseObject);
    }

    private static TestPickle Seeded()
    {
        var t = TestPickle.Create(width: 120, height: 30, start: true);
        var history = t.Runtime.History;
        Add(history, "git clone https://example.com/r.git", "/work", true);
        Add(history, "dotnet build", "/work/app", false);
        Add(history, "git status", "/work", true);
        return t;
    }

    private static void Add(IHistoryStore history, string command, string cwd, bool success)
    {
        history.Add(new HistoryEntry(command, DateTimeOffset.Now, cwd));
        history.CompleteLast(success, 10);
    }
}
