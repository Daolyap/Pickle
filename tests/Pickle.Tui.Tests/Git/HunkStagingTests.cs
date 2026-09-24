using Pickle.Abstractions;
using Pickle.Core.Git;
using Pickle.Tui.Panels.Git;

namespace Pickle.Tui.Tests.Git;

/// <summary>Patches built by <see cref="DiffParser"/> applied to a real index through <see cref="GitService"/>.</summary>
public sealed class HunkStagingTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "pickle-git-tests", Guid.NewGuid().ToString("N")[..12]);
    private readonly string _root;
    private readonly GitService _git = new(new NullLog());

    public HunkStagingTests()
    {
        Assert.SkipWhen(_git.GitPath is null, "git is not installed");
        _root = Path.Combine(_base, "repo");
        Directory.CreateDirectory(_root);
        var emptyConfig = Path.Combine(_base, "empty.gitconfig");
        File.WriteAllText(emptyConfig, string.Empty);
        _git.ExtraEnvironment["GIT_CONFIG_GLOBAL"] = emptyConfig;
        _git.ExtraEnvironment["GIT_CONFIG_NOSYSTEM"] = "1";
        _git.ExtraEnvironment["GIT_AUTHOR_NAME"] = _git.ExtraEnvironment["GIT_COMMITTER_NAME"] = "Pickle Test";
        _git.ExtraEnvironment["GIT_AUTHOR_EMAIL"] = _git.ExtraEnvironment["GIT_COMMITTER_EMAIL"] = "test@pickle.invalid";
        Git("init", "-q", "--initial-branch=main");
    }

    [Fact]
    public async Task StagesAndUnstagesASingleHunk()
    {
        var lines = Enumerable.Range(1, 30).Select(i => $"line {i}").ToList();
        Write("f.txt", string.Join('\n', lines) + "\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "init");

        lines[1] = "LINE 2";
        lines[25] = "LINE 26";
        Write("f.txt", string.Join('\n', lines) + "\n");

        var file = Assert.Single(DiffParser.Parse(await _git.GetDiffAsync(_root, "f.txt", staged: false)));
        Assert.Equal(2, file.Hunks.Count);

        var apply = await _git.ApplyPatchToIndexAsync(_root, DiffParser.BuildHunkPatch(file, file.Hunks[1]), reverse: false);
        Assert.True(apply.Success, apply.Error);

        var staged = Assert.Single(DiffParser.Parse(await _git.GetDiffAsync(_root, "f.txt", staged: true)));
        Assert.Equal(["-line 26", "+LINE 26"], staged.Hunks.Single().Lines.Where(l => l.IsChange).Select(l => l.Text));
        var unstaged = Assert.Single(DiffParser.Parse(await _git.GetDiffAsync(_root, "f.txt", staged: false)));
        Assert.Equal(["-line 2", "+LINE 2"], unstaged.Hunks.Single().Lines.Where(l => l.IsChange).Select(l => l.Text));

        var unstage = await _git.ApplyPatchToIndexAsync(_root, DiffParser.BuildHunkPatch(staged, staged.Hunks[0]), reverse: true);
        Assert.True(unstage.Success, unstage.Error);
        Assert.Equal(string.Empty, await _git.GetDiffAsync(_root, "f.txt", staged: true));
    }

    [Fact]
    public async Task StagesAndUnstagesIndividualLines()
    {
        Write("f.txt", "a\nb\nc\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "init");
        Write("f.txt", "a\nB\nc\nd\ne\n");

        var file = DiffParser.Parse(await _git.GetDiffAsync(_root, "f.txt", staged: false))[0];
        var hunk = Assert.Single(file.Hunks);
        var plusD = hunk.Lines.ToList().FindIndex(l => l.Text == "+d");
        var minusB = hunk.Lines.ToList().FindIndex(l => l.Text == "-b");

        var patch = DiffParser.BuildLinePatch(file, hunk, new HashSet<int> { plusD, minusB }, reverse: false)!;
        var apply = await _git.ApplyPatchToIndexAsync(_root, patch, reverse: false);
        Assert.True(apply.Success, apply.Error + "\n" + patch);
        Assert.Equal("a\nc\nd\n", Git("show", ":f.txt"));

        var cached = DiffParser.Parse(await _git.GetDiffAsync(_root, "f.txt", staged: true))[0];
        var cachedHunk = cached.Hunks.Single();
        var index = cachedHunk.Lines.ToList().FindIndex(l => l.Text == "-b");
        var reverse = DiffParser.BuildLinePatch(cached, cachedHunk, new HashSet<int> { index }, reverse: true)!;
        var unapply = await _git.ApplyPatchToIndexAsync(_root, reverse, reverse: true);
        Assert.True(unapply.Success, unapply.Error + "\n" + reverse);
        Assert.Equal("a\nb\nc\nd\n", Git("show", ":f.txt"));
        Assert.Equal("a\nB\nc\nd\ne\n", File.ReadAllText(Path.Combine(_root, "f.txt")));
    }

    [Fact]
    public async Task StagesPartOfAnUntrackedFileAndOfACrlfFile()
    {
        Write("crlf.txt", "one\r\ntwo\r\nthree\r\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "init");
        Write("crlf.txt", "one\r\nTWO\r\nthree\r\nfour\r\n");
        Write("new.txt", "x\ny\nz\n");

        var untracked = DiffParser.Parse(await _git.GetDiffAsync(_root, "new.txt", staged: false))[0];
        Assert.True(untracked.IsNewFile);
        var patch = DiffParser.BuildLinePatch(untracked, untracked.Hunks[0], new HashSet<int> { 0, 2 }, reverse: false)!;
        var apply = await _git.ApplyPatchToIndexAsync(_root, patch, reverse: false);
        Assert.True(apply.Success, apply.Error);
        Assert.Equal("x\nz\n", Git("show", ":new.txt"));

        var crlf = DiffParser.Parse(await _git.GetDiffAsync(_root, "crlf.txt", staged: false))[0];
        var hunk = crlf.Hunks[0];
        var four = hunk.Lines.ToList().FindIndex(l => l.Text == "+four\r");
        var crlfPatch = DiffParser.BuildLinePatch(crlf, hunk, new HashSet<int> { four }, reverse: false)!;
        var applyCrlf = await _git.ApplyPatchToIndexAsync(_root, crlfPatch, reverse: false);
        Assert.True(applyCrlf.Success, applyCrlf.Error);
        Assert.Equal("one\r\ntwo\r\nthree\r\nfour\r\n", Git("show", ":crlf.txt"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void Write(string name, string content) => File.WriteAllText(Path.Combine(_root, name), content);

    private string Git(params string[] args)
    {
        var result = _git.RunAsync(_root, args).GetAwaiter().GetResult();
        Assert.True(result.Success, $"git {string.Join(' ', args)}: {result.Error}");
        return result.Output;
    }

    private sealed class NullLog : IPickleLogger
    {
        public void Log(PickleLogLevel level, string category, string message, Exception? exception = null)
        {
        }
    }
}
