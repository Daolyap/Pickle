using System.Globalization;
using System.Text;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Core.Git;

/// <summary>
/// <see cref="IGitService"/> over the git CLI: argv lists only (no shell), porcelain/NUL-separated formats, UTF-8,
/// <c>GIT_TERMINAL_PROMPT=0</c>, timeouts (status 2 s, local 15 s, network/hooks 120 s) and process-tree kill on
/// cancellation. Path arguments are repository-root relative (as reported by <see cref="GetStatusAsync"/>).
/// </summary>
public sealed class GitService(IPickleLogger log) : IGitService
{
    internal static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan LocalTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(120);

    private const string Category = "git";
    private const string NotFoundMessage = "git was not found. Install Git and make sure it is on PATH.";

    internal static readonly TimeSpan FilterScanLifetime = TimeSpan.FromSeconds(30);

    private readonly Lazy<string?> _located = new(() => GitProcess.Locate());
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Stamp, long At, IReadOnlyList<string> Overrides)> _filterScans = new(StringComparer.Ordinal);
    private string? _gitPathOverride;

    /// <summary>Extra environment variables for every git process (null removes one); tests use it to isolate config.</summary>
    public IDictionary<string, string?> ExtraEnvironment { get; } = new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>Absolute path of the git executable in use (found on PATH, or the Git for Windows default location).</summary>
    public string? GitPath
    {
        get => _gitPathOverride ?? _located.Value;
        internal set => _gitPathOverride = value;
    }

    public bool IsGitAvailable => GitPath is not null;

    public Task<string?> FindRepositoryRootAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(GitStatusParser.FindRoot(path));

    public async Task<GitStatus?> GetStatusAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!IsGitAvailable || GitStatusParser.FindRoot(path) is not { } root)
        {
            return null;
        }

        if (await RepositoryFilterOverridesAsync(root, cancellationToken).ConfigureAwait(false) is not { } overrides)
        {
            return null;
        }

        var result = await ExecAsync(
            root,
            [.. overrides, "--no-optional-locks", "status", "--porcelain=v2", "--branch", "--show-stash", "--ignore-submodules=dirty", "-z"],
            StatusTimeout,
            cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            log.Debug(Category, $"status failed in {root}: {result.Error.Trim()}");
            return null;
        }

        return GitStatusParser.Parse(result.Output, root) with
        {
            Operation = GitStatusParser.DetectOperation(GitStatusParser.ResolveGitDir(root)),
        };
    }

    public async Task<string> GetDiffAsync(string repo, string? file, bool staged, CancellationToken cancellationToken = default)
    {
        if (!IsGitAvailable)
        {
            return string.Empty;
        }

        var root = RootOf(repo);
        List<string> args = ["diff"];
        if (staged)
        {
            args.Add("--cached");
        }

        // Fixed prefixes keep patches appliable whatever diff.noprefix / diff.mnemonicPrefix say.
        args.AddRange(["--no-color", "--no-ext-diff", "--no-textconv", "--ignore-submodules=dirty", "--src-prefix=a/", "--dst-prefix=b/"]);
        if (file is not null)
        {
            args.Add("--");
            args.Add(file);
        }

        if (await RepositoryFilterOverridesAsync(root, cancellationToken).ConfigureAwait(false) is not { } overrides)
        {
            return string.Empty;
        }

        var result = await ExecAsync(root, [.. overrides, .. args], LocalTimeout, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            log.Debug(Category, $"diff failed: {result.Error.Trim()}");
            return string.Empty;
        }

        if (result.Output.Length > 0 || staged || file is null)
        {
            return result.Output;
        }

        var untracked = await ExecAsync(root, ["ls-files", "-z", "--others", "--exclude-standard", "--", file], LocalTimeout, cancellationToken).ConfigureAwait(false);
        if (!untracked.Success)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var path in GitOutputParsers.SplitNul(untracked.Output).Take(200))
        {
            sb.Append(UntrackedDiff.Build(root, path));
        }

        return sb.ToString();
    }

    public Task<GitCommandResult> StageAsync(string repo, IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
        paths.Count == 0 ? NoPaths() : RunInRootAsync(repo, ["add", "-A", "--", .. paths], LocalTimeout, cancellationToken);

    public Task<GitCommandResult> UnstageAsync(string repo, IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
        paths.Count == 0 ? NoPaths() : RunInRootAsync(repo, ["reset", "-q", "--", .. paths], LocalTimeout, cancellationToken);

    /// <summary>Drops unstaged changes: tracked paths are restored from the index, untracked files are deleted.</summary>
    public async Task<GitCommandResult> DiscardAsync(string repo, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0)
        {
            return await NoPaths().ConfigureAwait(false);
        }

        if (!IsGitAvailable)
        {
            return NotFound();
        }

        var root = RootOf(repo);
        var tracked = await ExecAsync(root, ["ls-files", "-z", "--", .. paths], LocalTimeout, cancellationToken).ConfigureAwait(false);
        var untracked = await ExecAsync(root, ["ls-files", "-z", "--others", "--exclude-standard", "--", .. paths], LocalTimeout, cancellationToken).ConfigureAwait(false);
        if (!tracked.Success || !untracked.Success)
        {
            return ToResult(tracked.Success ? untracked : tracked);
        }

        var trackedFiles = GitOutputParsers.SplitNul(tracked.Output);
        var untrackedFiles = GitOutputParsers.SplitNul(untracked.Output);
        var restore = paths.Where(p => trackedFiles.Any(f => IsUnder(f, p))).ToList();
        var clean = paths.Where(p => untrackedFiles.Any(f => IsUnder(f, p))).ToList();
        if (restore.Count == 0 && clean.Count == 0)
        {
            return new GitCommandResult(true, string.Empty, string.Empty, 0);
        }

        var output = new StringBuilder();
        var error = new StringBuilder();
        var exitCode = 0;
        if (restore.Count > 0)
        {
            var r = await ExecAsync(root, ["checkout", "-q", "--", .. restore], LocalTimeout, cancellationToken).ConfigureAwait(false);
            output.Append(r.Output);
            error.Append(r.Error);
            exitCode = r.Success ? 0 : r.ExitCode;
        }

        if (clean.Count > 0 && exitCode == 0)
        {
            var r = await ExecAsync(root, ["clean", "-f", "-d", "-q", "--", .. clean], LocalTimeout, cancellationToken).ConfigureAwait(false);
            output.Append(r.Output);
            error.Append(r.Error);
            exitCode = r.Success ? 0 : r.ExitCode;
        }

        return new GitCommandResult(exitCode == 0, output.ToString(), error.ToString().TrimEnd(), exitCode);
    }

    public Task<GitCommandResult> ApplyPatchToIndexAsync(string repo, string patch, bool reverse, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(patch))
        {
            return Task.FromResult(Fail("The patch is empty."));
        }

        // U+FFFD means the diff was not valid UTF-8; applying the decoded text would corrupt the file's bytes.
        if (patch.Contains('�', StringComparison.Ordinal))
        {
            return Task.FromResult(Fail("This file is not UTF-8 text; stage the whole file instead."));
        }

        if (!patch.EndsWith('\n'))
        {
            patch += "\n";
        }

        List<string> args = ["apply", "--cached", "--recount", "--whitespace=nowarn"];
        if (reverse)
        {
            args.Add("--reverse");
        }

        args.Add("-");
        return RunInRootAsync(repo, args, LocalTimeout, cancellationToken, stdin: patch);
    }

    public Task<GitCommandResult> CommitAsync(string repo, string message, bool amend = false, CancellationToken cancellationToken = default)
    {
        message = (message ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(message))
        {
            return amend
                ? RunInRootAsync(repo, ["commit", "--amend", "--no-edit"], NetworkTimeout, cancellationToken)
                : Task.FromResult(Fail("The commit message is empty."));
        }

        // Hooks (pre-commit, commit-msg) may be slow, hence the long timeout.
        List<string> args = ["commit", "-F", "-"];
        if (amend)
        {
            args.Add("--amend");
        }

        return RunInRootAsync(repo, args, NetworkTimeout, cancellationToken, stdin: message);
    }

    public async Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string repo, bool includeRemote = true, CancellationToken cancellationToken = default)
    {
        if (!IsGitAvailable)
        {
            return [];
        }

        List<string> args = ["for-each-ref", GitOutputParsers.BranchFormat, "refs/heads"];
        if (includeRemote)
        {
            args.Add("refs/remotes");
        }

        var result = await ExecAsync(RootOf(repo), args, LocalTimeout, cancellationToken).ConfigureAwait(false);
        return result.Success ? GitOutputParsers.ParseBranches(result.Output) : [];
    }

    public Task<GitCommandResult> CheckoutAsync(string repo, string branch, bool create = false, CancellationToken cancellationToken = default)
    {
        if (create)
        {
            return GitRefNames.IsValidBranchName(branch)
                ? RunInRootAsync(repo, ["checkout", "-b", branch], LocalTimeout, cancellationToken)
                : Task.FromResult(Fail($"'{branch}' is not a valid branch name."));
        }

        // "<rev> --" never treats the name as a path, and still creates a tracking branch for a unique remote match.
        return GitRefNames.IsSafeRevision(branch)
            ? RunInRootAsync(repo, ["checkout", branch, "--"], LocalTimeout, cancellationToken)
            : Task.FromResult(Fail($"'{branch}' is not a valid branch or revision."));
    }

    public Task<GitCommandResult> DeleteBranchAsync(string repo, string branch, bool force = false, CancellationToken cancellationToken = default) =>
        GitRefNames.IsValidBranchName(branch)
            ? RunInRootAsync(repo, ["branch", force ? "-D" : "-d", branch], LocalTimeout, cancellationToken)
            : Task.FromResult(Fail($"'{branch}' is not a valid branch name."));

    public async Task<IReadOnlyList<GitCommit>> GetLogAsync(string repo, int max = 200, bool graph = true, CancellationToken cancellationToken = default)
    {
        if (!IsGitAvailable || max <= 0)
        {
            return [];
        }

        List<string> args = ["log", "--max-count=" + max.ToString(CultureInfo.InvariantCulture), GitOutputParsers.LogFormat];
        if (graph)
        {
            args.Add("--graph");
        }

        var result = await ExecAsync(RootOf(repo), args, LocalTimeout, cancellationToken).ConfigureAwait(false);

        // An unborn branch (no commits yet) makes git log fail; that's just an empty history.
        return result.Success ? GitOutputParsers.ParseLog(result.Output) : [];
    }

    public async Task<IReadOnlyList<GitStash>> GetStashesAsync(string repo, CancellationToken cancellationToken = default)
    {
        if (!IsGitAvailable)
        {
            return [];
        }

        var result = await ExecAsync(RootOf(repo), ["stash", "list", GitOutputParsers.StashFormat], LocalTimeout, cancellationToken).ConfigureAwait(false);
        return result.Success ? GitOutputParsers.ParseStashes(result.Output) : [];
    }

    public Task<GitCommandResult> StashPushAsync(string repo, string? message, bool includeUntracked, CancellationToken cancellationToken = default)
    {
        List<string> args = ["stash", "push"];
        if (includeUntracked)
        {
            args.Add("--include-untracked");
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            args.Add("--message=" + message.ReplaceLineEndings(" ").Trim());
        }

        return RunInRootAsync(repo, args, LocalTimeout, cancellationToken);
    }

    public Task<GitCommandResult> StashApplyAsync(string repo, int index, bool pop, CancellationToken cancellationToken = default) =>
        index < 0
            ? Task.FromResult(Fail("Invalid stash index."))
            : RunInRootAsync(repo, ["stash", pop ? "pop" : "apply", StashRef(index)], LocalTimeout, cancellationToken);

    public Task<GitCommandResult> StashDropAsync(string repo, int index, CancellationToken cancellationToken = default) =>
        index < 0
            ? Task.FromResult(Fail("Invalid stash index."))
            : RunInRootAsync(repo, ["stash", "drop", StashRef(index)], LocalTimeout, cancellationToken);

    public Task<GitCommandResult> FetchAsync(string repo, CancellationToken cancellationToken = default) =>
        RunInRootAsync(repo, ["fetch"], NetworkTimeout, cancellationToken);

    public Task<GitCommandResult> PullAsync(string repo, CancellationToken cancellationToken = default) =>
        RunInRootAsync(repo, ["pull"], NetworkTimeout, cancellationToken);

    public async Task<GitCommandResult> PushAsync(string repo, bool setUpstream = false, CancellationToken cancellationToken = default)
    {
        if (!setUpstream)
        {
            return await RunInRootAsync(repo, ["push"], NetworkTimeout, cancellationToken).ConfigureAwait(false);
        }

        if (!IsGitAvailable)
        {
            return NotFound();
        }

        var root = RootOf(repo);
        var head = await ExecAsync(root, ["symbolic-ref", "--quiet", "--short", "HEAD"], LocalTimeout, cancellationToken).ConfigureAwait(false);
        var branch = head.Output.Trim();
        if (!head.Success || !GitRefNames.IsValidBranchName(branch))
        {
            return Fail("HEAD is not on a branch; check out a branch before pushing.");
        }

        var remotes = await ExecAsync(root, ["remote"], LocalTimeout, cancellationToken).ConfigureAwait(false);
        var names = GitOutputParsers.SplitLines(remotes.Output);
        var remote = names.Contains("origin") ? "origin" : names.Count == 1 ? names[0] : null;
        if (remote is null || !GitRefNames.IsSafeRevision(remote))
        {
            return Fail(names.Count == 0 ? "This repository has no remote to push to." : "No remote named 'origin'; push from the command line to pick one.");
        }

        return await RunInRootAsync(repo, ["push", "-u", remote, branch], NetworkTimeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitCommandResult> RunAsync(string repo, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        if (!IsGitAvailable)
        {
            return NotFound();
        }

        var result = await ExecAsync(repo, args, NetworkTimeout, cancellationToken, literalPathspecs: false).ConfigureAwait(false);
        return ToResult(result);
    }

    private async Task<GitCommandResult> RunInRootAsync(string repo, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken cancellationToken, string? stdin = null)
    {
        if (!IsGitAvailable)
        {
            return NotFound();
        }

        var result = await ExecAsync(RootOf(repo), args, timeout, cancellationToken, stdin: stdin).ConfigureAwait(false);
        if (!result.Success)
        {
            log.Debug(Category, $"git {string.Join(' ', args.Take(3))} failed ({result.ExitCode}): {result.Error.Trim()}");
        }

        return ToResult(result);
    }

    private Task<GitProcessResult> ExecAsync(
        string workingDirectory,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? stdin = null,
        bool? literalPathspecs = null)
    {
        if (GitPath is not { } git)
        {
            return Task.FromResult(new GitProcessResult(-1, string.Empty, NotFoundMessage, false));
        }

        // quotepath=off: UTF-8 paths verbatim. fsmonitor=false, log.showSignature=false: a repository's config can
        // name programs (fsmonitor hook, gpg.program) that status/log would run (the prompt runs status anywhere). literal-pathspecs (only where paths
        // follow "--"; it breaks `stash push -u`): file names like ":x" or "a[1]" are never pathspec magic or globs.
        List<string> argv = ["-c", "core.quotepath=off", "-c", "color.ui=false", "-c", "core.fsmonitor=false", "-c", "log.showSignature=false"];
        if (literalPathspecs ?? args.Contains("--"))
        {
            argv.Add("--literal-pathspecs");
        }

        argv.AddRange(args);
        var environment = ExtraEnvironment.Count == 0
            ? (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>()
            : new Dictionary<string, string?>(ExtraEnvironment);
        return GitProcess.RunAsync(git, workingDirectory, argv, timeout, environment, stdin, cancellationToken);
    }

    /// <summary>
    /// <c>-c filter.&lt;driver&gt;.clean=</c> (etc.) for every filter command set in the repository's own config.
    /// `git status`/`git diff` run clean filters on files whose stat data changed, and the prompt runs status on every
    /// cd, so a .git/config shipped in an archive would otherwise run its commands. Global/system config (git-lfs) is
    /// the user's and stays active. Submodules (whose own config would apply) are kept out with
    /// --ignore-submodules=dirty. Null when the config can't be read safely (the caller then skips the command).
    /// Cached per repository while its config files are unchanged (and at most <see cref="FilterScanLifetime"/>).
    /// </summary>
    internal async Task<IReadOnlyList<string>?> RepositoryFilterOverridesAsync(string root, CancellationToken cancellationToken)
    {
        var stamp = ConfigStamp(root);
        if (_filterScans.TryGetValue(root, out var cached) && cached.Stamp == stamp && Environment.TickCount64 - cached.At < FilterScanLifetime.TotalMilliseconds)
        {
            return cached.Overrides;
        }

        var overrides = await ScanRepositoryFiltersAsync(root, cancellationToken).ConfigureAwait(false);
        if (overrides is not null)
        {
            _filterScans[root] = (stamp, Environment.TickCount64, overrides);
        }

        return overrides;
    }

    private async Task<IReadOnlyList<string>?> ScanRepositoryFiltersAsync(string root, CancellationToken cancellationToken)
    {
        const string pattern = @"^filter\..+\.(clean|smudge|process)$";
        var result = await ExecAsync(root, ["config", "--includes", "--show-scope", "--name-only", "--get-regexp", pattern], StatusTimeout, cancellationToken).ConfigureAwait(false);
        var scoped = true;
        if (result.ExitCode == 129 && !result.TimedOut)
        {
            // git < 2.26 has no --show-scope: read the repository's own files only.
            result = await ExecAsync(root, ["config", "--local", "--includes", "--name-only", "--get-regexp", pattern], StatusTimeout, cancellationToken).ConfigureAwait(false);
            scoped = false;
        }

        if (result.ExitCode == 1 && !result.TimedOut && result.Output.Length == 0)
        {
            return [];
        }

        if (!result.Success)
        {
            log.Debug(Category, $"config scan failed in {root} ({result.ExitCode}): {result.Error.Trim()}");
            return null;
        }

        var overrides = new List<string>();
        var drivers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = scoped ? line.Split('\t', 2) : ["local", line];
            if (parts.Length != 2 || parts[1].Contains('=', StringComparison.Ordinal))
            {
                // `-c` splits at the first '=', so such a driver name can't be overridden.
                log.Debug(Category, $"status skipped in {root}: unexpected filter config '{line}'");
                return null;
            }

            if (parts[0] is not ("global" or "system"))
            {
                overrides.Add("-c");
                overrides.Add(parts[1] + "=");
                drivers.Add(parts[1][..parts[1].LastIndexOf('.')]);
            }
        }

        // A required filter without a command is an error; compare the raw bytes instead.
        foreach (var driver in drivers)
        {
            overrides.Add("-c");
            overrides.Add(driver + ".required=false");
        }

        return overrides;
    }

    private static string ConfigStamp(string root)
    {
        if (GitStatusParser.ResolveGitDir(root) is not { } gitDir)
        {
            return string.Empty;
        }

        var common = gitDir;
        try
        {
            var commonFile = Path.Combine(gitDir, "commondir");
            if (File.Exists(commonFile))
            {
                common = Path.GetFullPath(Path.Combine(gitDir, File.ReadAllText(commonFile).Trim()));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }

        return string.Join('|', new[] { Path.Combine(common, "config"), Path.Combine(gitDir, "config.worktree") }.Select(path =>
        {
            var info = new FileInfo(path);
            return info.Exists ? info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) + ":" + info.Length.ToString(CultureInfo.InvariantCulture) : "-";
        }));
    }

    private static string RootOf(string repo) => GitStatusParser.FindRoot(repo) ?? repo;

    private static bool IsUnder(string file, string pathspec)
    {
        var spec = pathspec.Replace('\\', '/').TrimEnd('/');
        return spec is "." or "" || file == spec || file.StartsWith(spec + "/", StringComparison.Ordinal);
    }

    private static string StashRef(int index) => "stash@{" + index.ToString(CultureInfo.InvariantCulture) + "}";

    private static GitCommandResult ToResult(GitProcessResult r) => new(r.Success, r.Output, r.Error.TrimEnd(), r.ExitCode);

    private static GitCommandResult Fail(string message) => new(false, string.Empty, message, -1);

    private static GitCommandResult NotFound() => Fail(NotFoundMessage);

    private static Task<GitCommandResult> NoPaths() => Task.FromResult(Fail("No paths given."));
}
