using Pickle.Abstractions.Services;

namespace Pickle.Core.Prompt.Segments;

/// <summary>
/// Everything the built-in segments read from the outside world, as swappable functions so segments stay pure
/// and the theme preview / tests can render against fixed sample data.
/// </summary>
public sealed class SegmentEnvironment
{
    public required Func<string?> Home { get; init; }

    public required Func<string, string?> FindRepositoryRoot { get; init; }

    public required Func<string, string?> GetEnvironmentVariable { get; init; }

    public required Func<string, CancellationToken, Task<GitStatus?>> GetGitStatus { get; init; }

    /// <summary>True when a package.json exists in the directory or one of its ancestors.</summary>
    public required Func<string, bool> IsNodeProject { get; init; }

    public required Func<CancellationToken, Task<string?>> GetNodeVersion { get; init; }

    /// <summary>Current kube context (optionally "context:namespace"), or null.</summary>
    public required Func<KubeContext?> GetKubeContext { get; init; }

    public required Func<int> DurationThresholdMs { get; init; }

    /// <summary>The live environment: real filesystem, process environment, git service, node, kubeconfig.</summary>
    public static SegmentEnvironment Create(Func<IGitService?> git, Func<int> durationThresholdMs)
    {
        var node = new NodeVersionProbe();
        var kube = new KubeConfigReader(Environment.GetEnvironmentVariable, HomeDirectory);
        return new SegmentEnvironment
        {
            Home = HomeDirectory,
            FindRepositoryRoot = FindGitRoot,
            GetEnvironmentVariable = Environment.GetEnvironmentVariable,
            GetGitStatus = (cwd, ct) => git()?.GetStatusAsync(cwd, ct) ?? Task.FromResult<GitStatus?>(null),
            IsNodeProject = dir => FindUpwards(dir, "package.json", directory: false) is not null,
            GetNodeVersion = node.GetVersionAsync,
            GetKubeContext = kube.Read,
            DurationThresholdMs = durationThresholdMs,
        };
    }

    public static string? HomeDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? null : home;
    }

    /// <summary>Nearest ancestor containing .git (a directory, or a file for worktrees/submodules).</summary>
    public static string? FindGitRoot(string path) => FindUpwards(path, dir =>
    {
        var git = Path.Combine(dir, ".git");
        return Directory.Exists(git) || File.Exists(git);
    });

    /// <summary>Returns the directory (at or above <paramref name="path"/>) that contains the file <paramref name="name"/>.</summary>
    public static string? FindUpwards(string path, string name, bool directory) =>
        FindUpwards(path, dir => directory ? Directory.Exists(Path.Combine(dir, name)) : File.Exists(Path.Combine(dir, name)));

    private static string? FindUpwards(string path, Func<string, bool> matches)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            var current = new DirectoryInfo(path);
            while (current is not null)
            {
                if (matches(current.FullName))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
        }

        return null;
    }
}
