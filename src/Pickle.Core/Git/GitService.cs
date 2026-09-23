using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Core.Git;

/// <summary>
/// FOUNDATION PLACEHOLDER (workstream W7 replaces this file). W7 implements every IGitService member on top of the
/// git CLI (argv lists via ProcessStartInfo.ArgumentList, porcelain v2 parsing, timeouts).
/// </summary>
public sealed class GitService : IGitService
{
    private readonly IPickleLogger _log;

    public GitService(IPickleLogger log) => _log = log;

    public bool IsGitAvailable => false;

    public Task<string?> FindRepositoryRootAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

    public Task<GitStatus?> GetStatusAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult<GitStatus?>(null);

    public Task<string> GetDiffAsync(string repo, string? file, bool staged, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);

    public Task<GitCommandResult> StageAsync(string repo, IReadOnlyList<string> paths, CancellationToken cancellationToken = default) => NotImplemented();

    public Task<GitCommandResult> UnstageAsync(string repo, IReadOnlyList<string> paths, CancellationToken cancellationToken = default) => NotImplemented();

    public Task<GitCommandResult> DiscardAsync(string repo, IReadOnlyList<string> paths, CancellationToken cancellationToken = default) => NotImplemented();

    public Task<GitCommandResult> ApplyPatchToIndexAsync(string repo, string patch, bool reverse, CancellationToken cancellationToken = default) => NotImplemented();

    public Task<GitCommandResult> CommitAsync(string repo, string message, bool amend = false, CancellationToken cancellationToken = default) => NotImplemented();

    public Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string repo, bool includeRemote = true, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GitBranch>>([]);

    public Task<GitCommandResult> CheckoutAsync(string repo, string branch, bool create = false, CancellationToken cancellationToken = default) => NotImplemented();

    public Task<GitCommandResult> DeleteBranchAsync(string repo, string branch, bool force = false, CancellationToken cancellationToken = default) => NotImplemented();

    public Task<IReadOnlyList<GitCommit>> GetLogAsync(string repo, int max = 200, bool graph = true, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GitCommit>>([]);

    public Task<IReadOnlyList<GitStash>> GetStashesAsync(string repo, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GitStash>>([]);

    public Task<GitCommandResult> StashPushAsync(string repo, string? message, bool includeUntracked, CancellationToken cancellationToken = default) => NotImplemented();

    public Task<GitCommandResult> StashApplyAsync(string repo, int index, bool pop, CancellationToken cancellationToken = default) => NotImplemented();

    public Task<GitCommandResult> StashDropAsync(string repo, int index, CancellationToken cancellationToken = default) => NotImplemented();

    public Task<GitCommandResult> FetchAsync(string repo, CancellationToken cancellationToken = default) => NotImplemented();

    public Task<GitCommandResult> PullAsync(string repo, CancellationToken cancellationToken = default) => NotImplemented();

    public Task<GitCommandResult> PushAsync(string repo, bool setUpstream = false, CancellationToken cancellationToken = default) => NotImplemented();

    public Task<GitCommandResult> RunAsync(string repo, IReadOnlyList<string> args, CancellationToken cancellationToken = default) => NotImplemented();

    private static Task<GitCommandResult> NotImplemented() => Task.FromResult(new GitCommandResult(false, string.Empty, "Git service not implemented yet.", -1));
}
