using System.Text.Json.Nodes;
using Pickle.Core.Sync;

namespace Pickle.Core.Tests.Sync;

public sealed class SyncEngineTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pickle-sync-engine").FullName;
    private readonly SyncState _state = new() { Backend = "folder" };

    public SyncEngineTests()
    {
        Directory.CreateDirectory(Local);
        Directory.CreateDirectory(Remote);
    }

    private string Local => Path.Combine(_root, "local");

    private string Remote => Path.Combine(_root, "remote");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private SyncResult Run(SyncDirection direction = SyncDirection.Both, bool history = true)
    {
        var result = new SyncEngine(Local, Remote).Run(_state, new SyncOptions(direction, history));
        _state.Items = result.NewBase;
        _state.LastSync = DateTimeOffset.UtcNow;
        return result;
    }

    private static void Write(string dir, string relative, string text, DateTime? time = null)
    {
        var path = Path.Combine(dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        if (time is { } t)
        {
            File.SetLastWriteTimeUtc(path, t);
        }
    }

    private static JsonNode Json(string dir, string relative) => JsonNode.Parse(File.ReadAllText(Path.Combine(dir, relative)))!;

    [Fact]
    public void FirstSyncCopiesEverythingSyncedButNeverLocalOnlyFiles()
    {
        Write(Local, "config.json", """{ "theme": "nord", "sync": { "backend": "folder", "target": "/x" } }""");
        Write(Local, "config.local.json", """{ "theme": "machine" }""");
        Write(Local, "aliases.json", """[ { "name": "ll", "body": "ls -l", "updatedAt": "2026-01-01T00:00:00+00:00" } ]""");
        Write(Local, "themes/mine.json", """{ "name": "mine" }""");
        Write(Local, "profile.ps1", "Set-Alias g git\n");
        Write(Local, "plugins/Foo/Foo.psm1", "# binary-ish plugin files are never synced\n");
        Write(Local, "history.jsonl", """{"commandLine":"ls","timestamp":"2026-01-01T00:00:00+00:00"}""" + "\n");

        var result = Run();

        Assert.Empty(result.Conflicts);
        Assert.Equal("nord", Json(Remote, "config.json")["theme"]!.GetValue<string>());
        Assert.Null(Json(Remote, "config.json")["sync"]);
        Assert.False(File.Exists(Path.Combine(Remote, "config.local.json")));
        Assert.False(Directory.Exists(Path.Combine(Remote, "plugins")));
        Assert.Equal("ll", Json(Remote, "aliases.json")[0]!["name"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(Remote, "themes", "mine.json")));
        Assert.True(File.Exists(Path.Combine(Remote, "profile.ps1")));
        Assert.Contains("\"ls\"", File.ReadAllText(Path.Combine(Remote, "history.jsonl")), StringComparison.Ordinal);
        Assert.Empty(Run().Changes);
    }

    [Fact]
    public void ChangesOnDifferentKeysMergeWithoutConflicts()
    {
        Write(Local, "config.json", """{ "theme": "a", "editor": { "bellStyle": "none" } }""");
        Run();

        Write(Local, "config.json", """{ "theme": "b", "editor": { "bellStyle": "none" } }""");
        Write(Remote, "config.json", """{ "theme": "a", "editor": { "bellStyle": "visual" }, "prompt": { "gitTimeoutMs": 9 } }""");
        var result = Run();

        Assert.Empty(result.Conflicts);
        foreach (var dir in new[] { Local, Remote })
        {
            var config = Json(dir, "config.json");
            Assert.Equal("b", config["theme"]!.GetValue<string>());
            Assert.Equal("visual", config["editor"]!["bellStyle"]!.GetValue<string>());
            Assert.Equal(9, config["prompt"]!["gitTimeoutMs"]!.GetValue<int>());
        }
    }

    [Fact]
    public void BothChangedTheNewerWinsAndTheConflictIsReported()
    {
        Write(Local, "config.json", """{ "theme": "a" }""");
        Run();

        Write(Local, "config.json", """{ "theme": "local" }""", DateTime.UtcNow.AddMinutes(-10));
        Write(Remote, "config.json", """{ "theme": "remote" }""", DateTime.UtcNow);
        var result = Run();

        var conflict = Assert.Single(result.Conflicts);
        Assert.Contains("config theme", conflict, StringComparison.Ordinal);
        Assert.Contains("remote", conflict, StringComparison.Ordinal);
        Assert.Equal("remote", Json(Local, "config.json")["theme"]!.GetValue<string>());
    }

    [Fact]
    public void AliasConflictsAreDecidedByUpdatedAt()
    {
        Write(Local, "aliases.json", """[ { "name": "g", "body": "git", "updatedAt": "2026-01-01T00:00:00+00:00" } ]""");
        Run();

        Write(Local, "aliases.json", """[ { "name": "g", "body": "git status", "updatedAt": "2026-03-01T00:00:00+00:00" }, { "name": "new", "body": "x", "updatedAt": "2026-03-01T00:00:00+00:00" } ]""");
        Write(Remote, "aliases.json", """[ { "name": "g", "body": "git log", "updatedAt": "2026-02-01T00:00:00+00:00" } ]""", DateTime.UtcNow.AddDays(1));
        var result = Run();

        Assert.Single(result.Conflicts);
        var remote = Json(Remote, "aliases.json").AsArray();
        Assert.Equal("git status", remote.Single(a => a!["name"]!.GetValue<string>() == "g")!["body"]!.GetValue<string>());
        Assert.Contains(remote, a => a!["name"]!.GetValue<string>() == "new");
    }

    [Fact]
    public void DeletionsPropagateAndHistoryIsAUnion()
    {
        Write(Local, "themes/old.json", "{}");
        Write(Local, "history.jsonl", """{"commandLine":"b","timestamp":"2026-01-02T00:00:00+00:00"}""" + "\n");
        Write(Remote, "history.jsonl", """{"commandLine":"a","timestamp":"2026-01-01T00:00:00+00:00"}""" + "\n" + """{"commandLine":"b","timestamp":"2026-01-02T00:00:00+00:00"}""" + "\n");
        Run();

        var lines = File.ReadAllLines(Path.Combine(Local, "history.jsonl"));
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"a\"", lines[0], StringComparison.Ordinal);
        Assert.Equal(File.ReadAllText(Path.Combine(Local, "history.jsonl")), File.ReadAllText(Path.Combine(Remote, "history.jsonl")));

        File.Delete(Path.Combine(Local, "themes", "old.json"));
        var result = Run(SyncDirection.Push);
        Assert.Contains("↑ removed themes/old.json", result.Changes);
        Assert.False(File.Exists(Path.Combine(Remote, "themes", "old.json")));
    }

    [Fact]
    public void PullNeverWritesTheRemoteAndPushNeverWritesLocal()
    {
        Write(Local, "config.json", """{ "theme": "a" }""");
        Run();
        Write(Local, "config.json", """{ "theme": "local-change" }""");
        Write(Remote, "profile.ps1", "# remote\n");

        var pull = Run(SyncDirection.Pull);
        Assert.Equal("a", Json(Remote, "config.json")["theme"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(Local, "profile.ps1")));
        Assert.Equal(1, pull.Pending);

        var push = Run(SyncDirection.Push);
        Assert.Contains("↑ config theme", push.Changes);
        Assert.Equal("local-change", Json(Remote, "config.json")["theme"]!.GetValue<string>());
        Assert.Empty(Run().Changes);
    }
}
