using System.Text.Json;
using Pickle.Abstractions;
using Pickle.Core.History;
using Pickle.Testing;

namespace Pickle.Core.Tests.History;

public class JsonlHistoryStoreTests
{
    [Fact]
    public void WritesOneJsonLineWhenTheCommandCompletes()
    {
        using var t = TestPickle.Create();
        using var store = new JsonlHistoryStore(t.Runtime);

        Assert.True(store.Add(Entry("Get-ChildItem", "/work")));
        Assert.False(File.Exists(t.Paths.HistoryFile));

        store.CompleteLast(success: true, durationMs: 42);
        var lines = File.ReadAllLines(t.Paths.HistoryFile);
        var saved = Assert.Single(lines);
        using var json = JsonDocument.Parse(saved);
        Assert.Equal("Get-ChildItem", json.RootElement.GetProperty("commandLine").GetString());
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(42, json.RootElement.GetProperty("durationMs").GetInt64());
        Assert.Equal(store.SessionId, json.RootElement.GetProperty("sessionId").GetString());

        var entry = Assert.Single(store.Entries);
        Assert.Equal(true, entry.Success);
        Assert.Equal(42, entry.DurationMs);
    }

    [Fact]
    public void DisposeWritesAnUncompletedEntry()
    {
        using var t = TestPickle.Create();
        using (var store = new JsonlHistoryStore(t.Runtime))
        {
            store.Add(Entry("Start-Sleep 100"));
        }

        using var reloaded = new JsonlHistoryStore(t.Runtime);
        var entry = Assert.Single(reloaded.Entries);
        Assert.Equal("Start-Sleep 100", entry.CommandLine);
        Assert.Null(entry.Success);
    }

    [Fact]
    public void AnUncompletedEntryIsWrittenWhenTheNextOneIsAdded()
    {
        using var t = TestPickle.Create();
        using var store = new JsonlHistoryStore(t.Runtime);
        store.Add(Entry("first"));
        store.Add(Entry("second"));
        store.CompleteLast(false, 5);

        var lines = File.ReadAllLines(t.Paths.HistoryFile);
        Assert.Equal(2, lines.Length);
        Assert.Equal(["first", "second"], store.Entries.Select(e => e.CommandLine));
        Assert.Null(store.Entries[0].Success);
        Assert.Equal(false, store.Entries[1].Success);
    }

    [Fact]
    public void IgnoresLeadingSpaceAndBlankLines()
    {
        using var t = TestPickle.Create();
        using var store = new JsonlHistoryStore(t.Runtime);
        Assert.False(store.Add(Entry(" secret-ish")));
        Assert.False(store.Add(Entry("   ")));
        store.CompleteLast(true, 1);
        Assert.Empty(store.Entries);
        Assert.False(File.Exists(t.Paths.HistoryFile));
    }

    [Fact]
    public void LeadingSpaceIsKeptWhenTheFilterIsOff()
    {
        using var t = TestPickle.Create(configure: c => c.History.IgnoreLeadingSpace = false);
        using var store = new JsonlHistoryStore(t.Runtime);
        Assert.True(store.Add(Entry(" ls")));
    }

    [Fact]
    public void SkipsConsecutiveDuplicatesWithoutTouchingThePreviousEntry()
    {
        using var t = TestPickle.Create();
        using var store = new JsonlHistoryStore(t.Runtime);
        store.Add(Entry("ls"));
        store.CompleteLast(true, 1);

        Assert.False(store.Add(Entry("ls")));
        store.CompleteLast(false, 999);

        store.Add(Entry("pwd"));
        store.CompleteLast(true, 1);
        Assert.True(store.Add(Entry("ls")));
        store.CompleteLast(true, 1);

        Assert.Equal(["ls", "pwd", "ls"], store.Entries.Select(e => e.CommandLine));
        Assert.Equal(true, store.Entries[0].Success);
        Assert.Equal(3, File.ReadAllLines(t.Paths.HistoryFile).Length);
    }

    [Fact]
    public void DuplicatesAreKeptWhenTheFilterIsOff()
    {
        using var t = TestPickle.Create(configure: c => c.History.IgnoreDuplicates = false);
        using var store = new JsonlHistoryStore(t.Runtime);
        Assert.True(store.Add(Entry("ls")));
        Assert.True(store.Add(Entry("ls")));
        Assert.Equal(2, store.Entries.Count);
    }

    [Fact]
    public void SecretsStayInMemoryAndAreNeverWritten()
    {
        using var t = TestPickle.Create();
        using (var store = new JsonlHistoryStore(t.Runtime))
        {
            store.Add(Entry("git status"));
            store.CompleteLast(true, 1);
            store.Add(Entry("Connect-Thing -ApiKey 'sk-live-123'"));
            store.CompleteLast(true, 1);
            store.Add(Entry("$env:GITHUB_TOKEN = 'ghp_abcdefghijklmnopqrstuvwxyz0123456789'"));
            store.Add(Entry("Get-Date"));
            store.CompleteLast(true, 1);

            Assert.Equal(4, store.Entries.Count);
            Assert.True(store.IsMemoryOnly(store.Entries[1]));
            Assert.Equal(true, store.Entries[1].Success);
        }

        var file = File.ReadAllText(t.Paths.HistoryFile);
        Assert.DoesNotContain("sk-live", file, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_", file, StringComparison.Ordinal);

        using var reloaded = new JsonlHistoryStore(t.Runtime);
        Assert.Equal(["git status", "Get-Date"], reloaded.Entries.Select(e => e.CommandLine));
    }

    [Fact]
    public void SecretsAreWrittenWhenFilteringIsOff()
    {
        using var t = TestPickle.Create(configure: c => c.History.FilterSecrets = false);
        using var store = new JsonlHistoryStore(t.Runtime);
        store.Add(Entry("Connect-Thing -Token abc123"));
        store.CompleteLast(true, 1);
        Assert.Contains("abc123", File.ReadAllText(t.Paths.HistoryFile), StringComparison.Ordinal);
    }

    [Fact]
    public void TrimsTheFileToMaxEntriesOnceItExceedsTheSlack()
    {
        using var t = TestPickle.Create(configure: c => c.History.MaxEntries = 10);
        using var store = new JsonlHistoryStore(t.Runtime);
        for (var i = 1; i <= 12; i++)
        {
            store.Add(Entry($"cmd {i}"));
            store.CompleteLast(true, 1);
        }

        Assert.Equal(12, File.ReadAllLines(t.Paths.HistoryFile).Length);

        store.Add(Entry("cmd 13"));
        store.CompleteLast(true, 1);

        var lines = File.ReadAllLines(t.Paths.HistoryFile);
        Assert.Equal(10, lines.Length);
        Assert.Contains("cmd 4", lines[0], StringComparison.Ordinal);
        Assert.Contains("cmd 13", lines[^1], StringComparison.Ordinal);
        Assert.Equal(Enumerable.Range(4, 10).Select(i => $"cmd {i}"), store.Entries.Select(e => e.CommandLine));
        Assert.Empty(Directory.GetFiles(t.Paths.ConfigDir, "*.tmp"));
    }

    [Fact]
    public void TwoStoresShareOneFile()
    {
        using var t = TestPickle.Create();
        using var a = new JsonlHistoryStore(t.Runtime);
        using var b = new JsonlHistoryStore(t.Runtime);
        Assert.NotEqual(a.SessionId, b.SessionId);

        a.Add(Entry("from a"));
        a.CompleteLast(true, 1);
        Assert.Equal(["from a"], b.Entries.Select(e => e.CommandLine));

        b.Add(Entry("from b"));
        b.CompleteLast(true, 1);
        a.Add(Entry("from a again"));
        a.CompleteLast(true, 1);

        Assert.Equal(["from a", "from b", "from a again"], a.Entries.Select(e => e.CommandLine));
        Assert.Equal(["from a", "from b", "from a again"], b.Entries.Select(e => e.CommandLine));
        Assert.Equal(3, File.ReadAllLines(t.Paths.HistoryFile).Length);
    }

    [Fact]
    public void PicksUpARewriteThatKeepsTheFirstLine()
    {
        using var t = TestPickle.Create();
        using var store = new JsonlHistoryStore(t.Runtime);
        foreach (var command in new[] { "first", "third" })
        {
            store.Add(Entry(command));
            store.CompleteLast(true, 1);
        }

        Assert.Equal(["first", "third"], store.Entries.Select(e => e.CommandLine));

        // What `pk sync pull` does: merge in an older remote command, keeping timestamp order (same first line, longer file).
        var lines = File.ReadAllLines(t.Paths.HistoryFile).ToList();
        var remote = JsonSerializer.Serialize(new { commandLine = "second (remote)", timestamp = DateTimeOffset.Now, sessionId = "remote" });
        lines.Insert(1, remote);
        File.WriteAllLines(t.Paths.HistoryFile, lines);

        Assert.Equal(["first", "second (remote)", "third"], store.Entries.Select(e => e.CommandLine));
    }

    [Fact]
    public void SyncMergeKeepsLinesAppendedMeanwhileAndTheNewestMaxEntries()
    {
        using var t = TestPickle.Create();
        using var store = new JsonlHistoryStore(t.Runtime);
        store.Add(Entry("local"));
        store.CompleteLast(true, 1);
        var remoteRoot = Directory.CreateTempSubdirectory("pickle-sync-remote").FullName;
        try
        {
            var older = DateTimeOffset.Now.AddDays(-1);
            File.WriteAllLines(Path.Combine(remoteRoot, "history.jsonl"), Enumerable.Range(0, 3).Select(i =>
                JsonSerializer.Serialize(new { commandLine = $"remote {i}", timestamp = older.AddMinutes(i), sessionId = "remote" })));

            var engine = new Pickle.Core.Sync.SyncEngine(t.Paths.ConfigDir, remoteRoot);
            var result = engine.Run(new Pickle.Core.Sync.SyncState(), new Pickle.Core.Sync.SyncOptions(Pickle.Core.Sync.SyncDirection.Pull, HistoryMaxEntries: 3));
            Assert.True(result.LocalChanged);
            Assert.Equal(["remote 1", "remote 2", "local"], store.Entries.Select(e => e.CommandLine));
        }
        finally
        {
            Directory.Delete(remoteRoot, recursive: true);
        }
    }

    [Fact]
    public void PicksUpAClearFromAnotherSession()
    {
        using var t = TestPickle.Create();
        using var a = new JsonlHistoryStore(t.Runtime);
        using var b = new JsonlHistoryStore(t.Runtime);
        a.Add(Entry("one"));
        a.CompleteLast(true, 1);
        Assert.Single(b.Entries);

        b.Clear();
        Assert.Empty(b.Entries);
        Assert.Empty(a.Entries);

        b.Add(Entry("two"));
        b.CompleteLast(true, 1);
        Assert.Equal(["two"], a.Entries.Select(e => e.CommandLine));
    }

    [Fact]
    public void LoadsLazilyAndSkipsCorruptLines()
    {
        using var t = TestPickle.Create();
        File.WriteAllLines(t.Paths.HistoryFile,
        [
            """{"commandLine":"Get-Process","timestamp":"2026-01-02T03:04:05+00:00","cwd":"/tmp","success":true}""",
            "not json",
            "",
            """{"commandLine":"git log","timestamp":"2026-01-02T03:05:05+00:00"}""",
        ]);
        using var store = new JsonlHistoryStore(t.Runtime);
        Assert.Equal(["Get-Process", "git log"], store.Entries.Select(e => e.CommandLine));
        Assert.Equal("/tmp", store.Entries[0].Cwd);
    }

    [Fact]
    public void IgnoresAPartiallyWrittenLastLine()
    {
        using var t = TestPickle.Create();
        File.WriteAllText(t.Paths.HistoryFile, """{"commandLine":"done","timestamp":"2026-01-02T03:04:05+00:00"}""" + "\n" + """{"commandLine":"half""");
        using var store = new JsonlHistoryStore(t.Runtime);
        Assert.Equal(["done"], store.Entries.Select(e => e.CommandLine));

        File.AppendAllText(t.Paths.HistoryFile, """way","timestamp":"2026-01-02T03:04:06+00:00"}""" + "\n");
        Assert.Equal(["done", "halfway"], store.Entries.Select(e => e.CommandLine));
    }

    [Fact]
    public void ClearRemovesTheFile()
    {
        using var t = TestPickle.Create();
        using var store = new JsonlHistoryStore(t.Runtime);
        store.Add(Entry("x"));
        store.CompleteLast(true, 1);
        store.Clear();
        Assert.Empty(store.Entries);
        Assert.False(File.Exists(t.Paths.HistoryFile));
        store.CompleteLast(true, 1);
        Assert.False(File.Exists(t.Paths.HistoryFile));
    }

    [Fact]
    public void ReplRecordsExecutedLines()
    {
        using var t = TestPickle.Create(start: true);
        t.Runtime.Repl.ExecuteLine("Write-Output 'recorded'", echo: false);
        t.Runtime.Repl.ExecuteLine("Get-Item -LiteralPath '/definitely/missing'", echo: false);

        var entries = t.Runtime.History.Entries;
        Assert.Equal(["Write-Output 'recorded'", "Get-Item -LiteralPath '/definitely/missing'"], entries.Select(e => e.CommandLine));
        Assert.Equal(true, entries[0].Success);
        Assert.Equal(false, entries[1].Success);
        Assert.Equal(2, File.ReadAllLines(t.Paths.HistoryFile).Length);
    }

    private static HistoryEntry Entry(string command, string? cwd = null) => new(command, DateTimeOffset.Now, cwd);
}
