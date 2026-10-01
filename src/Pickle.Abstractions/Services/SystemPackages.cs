using System.Text.RegularExpressions;

namespace Pickle.Abstractions.Services;

public enum PackageAction
{
    Install,
    Remove,
    Upgrade,

    /// <summary>Upgrade everything that has an update (no package names).</summary>
    UpgradeAll,

    /// <summary>Refresh the package index (<c>apt-get update</c>); no package names.</summary>
    RefreshIndex,
}

/// <summary>A package of the system's package manager (apt, dnf, pacman, zypper, Homebrew).</summary>
public sealed record SystemPackage(string Name, string Version, string? Description = null)
{
    public bool Installed { get; init; } = true;

    /// <summary>The version an upgrade would install, or null when up to date (or unknown).</summary>
    public string? NewVersion { get; init; }

    public string? Repository { get; init; }

    public bool Upgradable => NewVersion is not null;
}

/// <summary>
/// The native package manager on Linux and macOS. Reading is unprivileged; changes are returned as <see cref="ShellCommand"/>
/// lines (with <c>sudo</c> where needed) so the shell shows the manager's own progress and password prompt, the same
/// way you would run them by hand.
/// </summary>
public interface ISystemPackageManager
{
    /// <summary>"apt", "dnf", "pacman", "zypper" or "Homebrew".</summary>
    string Name { get; }

    bool IsSupported { get; }

    /// <summary>False for Homebrew, which runs as the user.</summary>
    bool NeedsPrivileges { get; }

    Task<IReadOnlyList<SystemPackage>> ListInstalledAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SystemPackage>> ListUpgradesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SystemPackage>> SearchAsync(string query, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> InfoAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>The PowerShell line that performs <paramref name="action"/>; throws for names that are not package names.</summary>
    string ShellCommand(PackageAction action, IReadOnlyList<string> names);

    /// <summary>Runs the action without a terminal (root, polkit or <c>sudo -n</c>); a password prompt sets <see cref="ServiceOperationResult.ShellCommand"/>.</summary>
    Task<ServiceOperationResult> RunAsync(PackageAction action, IReadOnlyList<string> names, CancellationToken cancellationToken = default);
}

public static partial class PackageNames
{
    /// <summary>Plain package names only: letters, digits and <c>. + - _ @ : /</c>, never starting with a dash (an option).</summary>
    public static bool IsValid(string name) => Plain().IsMatch(name);

    public static void Require(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            if (!IsValid(name))
            {
                throw new ArgumentException($"'{name}' is not a package name.");
            }
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.+_@:/-]{0,127}$")]
    private static partial Regex Plain();
}
