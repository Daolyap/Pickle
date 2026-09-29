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

    // Optional inputs: left unset, their segments stay hidden.

    /// <summary>The default Azure subscription's name.</summary>
    public Func<string?> GetAzureSubscription { get; init; } = () => null;

    /// <summary>The active gcloud configuration's project.</summary>
    public Func<string?> GetGcloudProject { get; init; } = () => null;

    /// <summary>The Docker context from ~/.docker/config.json (DOCKER_CONTEXT is read through the environment).</summary>
    public Func<string?> GetDockerContext { get; init; } = () => null;

    /// <summary>The .NET SDK version for a directory, or null outside .NET projects.</summary>
    public Func<string, CancellationToken, Task<string?>> GetDotnetVersion { get; init; } = (_, _) => Task.FromResult<string?>(null);

    /// <summary>The Go version for a directory, or null outside Go modules.</summary>
    public Func<string, string?> GetGoVersion { get; init; } = _ => null;

    /// <summary>The Rust toolchain for a directory, or null outside Cargo projects.</summary>
    public Func<string, CancellationToken, Task<string?>> GetRustVersion { get; init; } = (_, _) => Task.FromResult<string?>(null);

    public Func<BatteryStatus?> GetBattery { get; init; } = () => null;

    /// <summary>The live environment: real filesystem, process environment, git service, node, kubeconfig.</summary>
    public static SegmentEnvironment Create(Func<IGitService?> git, Func<int> durationThresholdMs)
    {
        var node = new NodeVersionProbe();
        var kube = new KubeConfigReader(Environment.GetEnvironmentVariable, HomeDirectory);
        var cloud = new CloudConfigReader(Environment.GetEnvironmentVariable, HomeDirectory);
        var toolchains = new ToolchainReader();
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
            GetAzureSubscription = cloud.AzureSubscription,
            GetGcloudProject = cloud.GcloudProject,
            GetDockerContext = cloud.DockerContext,
            GetDotnetVersion = toolchains.DotnetAsync,
            GetGoVersion = toolchains.Go,
            GetRustVersion = toolchains.RustAsync,
            GetBattery = SystemMonitoring.SystemInfoProvider.Battery,
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
