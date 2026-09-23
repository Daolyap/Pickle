using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Core.Git;

namespace Pickle.Core.Tests.Git;

/// <summary>A throwaway repository driven through <see cref="GitService"/> with user/system git config isolated.</summary>
internal sealed class TempRepo : IDisposable
{
    private readonly string _base;

    private TempRepo(string baseDir, string root, GitService git)
    {
        _base = baseDir;
        Root = root;
        Git = git;
    }

    public string Root { get; }

    public GitService Git { get; }

    public string BaseDirectory => _base;

    public static bool GitInstalled { get; } = GitProcess.Locate() is not null;

    public static TempRepo Create(bool init = true, string name = "repo")
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "pickle-git-tests", Guid.NewGuid().ToString("N")[..12]);
        var root = Path.Combine(baseDir, name);
        Directory.CreateDirectory(root);
        var git = CreateService(baseDir);
        var repo = new TempRepo(baseDir, root, git);
        if (init)
        {
            repo.Run("init", "--initial-branch=main");
        }

        return repo;
    }

    public static GitService CreateService(string baseDir)
    {
        Directory.CreateDirectory(baseDir);
        var emptyConfig = Path.Combine(baseDir, "empty.gitconfig");
        File.WriteAllText(emptyConfig, string.Empty);
        var git = new GitService(NullLogger.Instance);
        git.ExtraEnvironment["GIT_CONFIG_GLOBAL"] = emptyConfig;
        git.ExtraEnvironment["GIT_CONFIG_NOSYSTEM"] = "1";
        git.ExtraEnvironment["GIT_AUTHOR_NAME"] = "Pickle Test";
        git.ExtraEnvironment["GIT_AUTHOR_EMAIL"] = "test@pickle.invalid";
        git.ExtraEnvironment["GIT_COMMITTER_NAME"] = "Pickle Test";
        git.ExtraEnvironment["GIT_COMMITTER_EMAIL"] = "test@pickle.invalid";
        return git;
    }

    /// <summary>Runs git in <see cref="Root"/> (or <paramref name="cwd"/>) and fails the test if it fails.</summary>
    public string Run(params string[] args) => RunIn(Root, args);

    public string RunIn(string cwd, params string[] args)
    {
        var result = Git.RunAsync(cwd, args).GetAwaiter().GetResult();
        Assert.True(result.Success, $"git {string.Join(' ', args)} failed ({result.ExitCode}): {result.Error}\n{result.Output}");
        return result.Output;
    }

    public GitCommandResult TryRun(params string[] args) => Git.RunAsync(Root, args).GetAwaiter().GetResult();

    public void Write(string relativePath, string content)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public string Read(string relativePath) => File.ReadAllText(Path.Combine(Root, relativePath));

    public bool Exists(string relativePath) => File.Exists(Path.Combine(Root, relativePath));

    public void CommitAll(string message)
    {
        Run("add", "-A");
        Run("commit", "-q", "-m", message);
    }

    public GitStatus Status() => Git.GetStatusAsync(Root).GetAwaiter().GetResult() ?? throw new InvalidOperationException("no status");

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_base, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_base, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class NullLogger : IPickleLogger
    {
        public static readonly NullLogger Instance = new();

        public void Log(PickleLogLevel level, string category, string message, Exception? exception = null)
        {
        }
    }
}
