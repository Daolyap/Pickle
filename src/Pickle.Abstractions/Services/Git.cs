namespace Pickle.Abstractions.Services;

public enum GitChangeKind
{
    None,
    Modified,
    Added,
    Deleted,
    Renamed,
    Copied,
    TypeChanged,
    Unmerged,
    Untracked,
    Ignored,
}

public sealed record GitStatusEntry(
    string Path,
    string? OriginalPath,
    GitChangeKind IndexStatus,
    GitChangeKind WorktreeStatus)
{
    public bool IsStaged => IndexStatus is not (GitChangeKind.None or GitChangeKind.Untracked or GitChangeKind.Ignored);
    public bool IsUnstaged => WorktreeStatus is not GitChangeKind.None && !IsConflicted;
    public bool IsUntracked => WorktreeStatus == GitChangeKind.Untracked;
    public bool IsConflicted => IndexStatus == GitChangeKind.Unmerged || WorktreeStatus == GitChangeKind.Unmerged;
}

public sealed record GitStatus(
    string Root,
    string? Branch,
    string? Upstream,
    int Ahead,
    int Behind,
    bool IsDetached,
    string? HeadSha,
    IReadOnlyList<GitStatusEntry> Entries,
    int StashCount,
    string? Operation = null)
{
    public int StagedCount => Entries.Count(e => e.IsStaged);
    public int UnstagedCount => Entries.Count(e => e.IsUnstaged && !e.IsUntracked);
    public int UntrackedCount => Entries.Count(e => e.IsUntracked);
    public int ConflictCount => Entries.Count(e => e.IsConflicted);
    public bool IsClean => Entries.Count == 0;
}

public sealed record GitBranch(string Name, bool IsCurrent, bool IsRemote, string? Upstream, string? LastCommitSubject, DateTimeOffset? LastCommitDate);

public sealed record GitCommit(string Sha, string ShortSha, string Author, DateTimeOffset Date, string Subject, IReadOnlyList<string> Refs, string Graph = "");

public sealed record GitStash(int Index, string Name, string Message);

public sealed record GitCommandResult(bool Success, string Output, string Error, int ExitCode);

public interface IGitService
{
    bool IsGitAvailable { get; }

    /// <summary>Repository root containing <paramref name="path"/>, or null.</summary>
    Task<string?> FindRepositoryRootAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Fast status (porcelain v2). Returns null outside a repository.</summary>
    Task<GitStatus?> GetStatusAsync(string path, CancellationToken cancellationToken = default);

    Task<string> GetDiffAsync(string repo, string? file, bool staged, CancellationToken cancellationToken = default);

    Task<GitCommandResult> StageAsync(string repo, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    Task<GitCommandResult> UnstageAsync(string repo, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    Task<GitCommandResult> DiscardAsync(string repo, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    /// <summary>Apply a unified-diff patch to the index (hunk staging) or, with <paramref name="reverse"/>, unstage it.</summary>
    Task<GitCommandResult> ApplyPatchToIndexAsync(string repo, string patch, bool reverse, CancellationToken cancellationToken = default);

    Task<GitCommandResult> CommitAsync(string repo, string message, bool amend = false, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string repo, bool includeRemote = true, CancellationToken cancellationToken = default);

    Task<GitCommandResult> CheckoutAsync(string repo, string branch, bool create = false, CancellationToken cancellationToken = default);

    Task<GitCommandResult> DeleteBranchAsync(string repo, string branch, bool force = false, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitCommit>> GetLogAsync(string repo, int max = 200, bool graph = true, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitStash>> GetStashesAsync(string repo, CancellationToken cancellationToken = default);

    Task<GitCommandResult> StashPushAsync(string repo, string? message, bool includeUntracked, CancellationToken cancellationToken = default);

    Task<GitCommandResult> StashApplyAsync(string repo, int index, bool pop, CancellationToken cancellationToken = default);

    Task<GitCommandResult> StashDropAsync(string repo, int index, CancellationToken cancellationToken = default);

    Task<GitCommandResult> FetchAsync(string repo, CancellationToken cancellationToken = default);

    Task<GitCommandResult> PullAsync(string repo, CancellationToken cancellationToken = default);

    Task<GitCommandResult> PushAsync(string repo, bool setUpstream = false, CancellationToken cancellationToken = default);

    /// <summary>Run an arbitrary git command (arguments are passed as an argv list, never through a shell).</summary>
    Task<GitCommandResult> RunAsync(string repo, IReadOnlyList<string> args, CancellationToken cancellationToken = default);
}
