using System.Text.RegularExpressions;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Admin.Packages;

/// <summary>Shared plumbing: program runner, privileged commands and the one rule that package names are validated before they reach any command line.</summary>
public abstract partial class PackageManagerBase(IProgramRunner runner, IPrivilegeService privilege) : ISystemPackageManager
{
    protected IProgramRunner Runner { get; } = runner;

    public abstract string Name { get; }

    public abstract bool IsSupported { get; }

    public virtual bool NeedsPrivileges => true;

    protected abstract string Program { get; }

    public abstract Task<IReadOnlyList<SystemPackage>> ListInstalledAsync(CancellationToken cancellationToken = default);

    public abstract Task<IReadOnlyList<SystemPackage>> ListUpgradesAsync(CancellationToken cancellationToken = default);

    public abstract Task<IReadOnlyList<SystemPackage>> SearchAsync(string query, CancellationToken cancellationToken = default);

    public abstract Task<IReadOnlyList<string>> InfoAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>The manager's arguments for an action (names already validated); program is <see cref="Program"/>.</summary>
    protected abstract IReadOnlyList<string> Arguments(PackageAction action, IReadOnlyList<string> names);

    protected PrivilegedCommand Command(PackageAction action, IReadOnlyList<string> names)
    {
        PackageNames.Require(names);
        if (action is PackageAction.Install or PackageAction.Remove or PackageAction.Upgrade && names.Count == 0)
        {
            throw new ArgumentException($"{action} needs at least one package name.");
        }

        return new PrivilegedCommand(Program, Arguments(action, names), $"{action} {string.Join(' ', names)}".Trim());
    }

    public string ShellCommand(PackageAction action, IReadOnlyList<string> names)
    {
        var command = Command(action, names);
        return NeedsPrivileges ? privilege.ShellCommand(command) : PowerShellQuote.Command(command.Program, command.Arguments);
    }

    public async Task<ServiceOperationResult> RunAsync(PackageAction action, IReadOnlyList<string> names, CancellationToken cancellationToken = default)
    {
        var command = Command(action, names);
        if (!NeedsPrivileges)
        {
            var result = await Runner.RunAsync(command.Program, command.Arguments, new ProgramRunOptions { Timeout = TimeSpan.FromMinutes(30) }, cancellationToken).ConfigureAwait(false);
            return new ServiceOperationResult(result.Success, result.Success ? command.Reason + " done." : result.Message);
        }

        var outcome = await privilege.RunAsync(command, cancellationToken).ConfigureAwait(false);
        return new ServiceOperationResult(outcome.Success, outcome.Message) { ShellCommand = outcome.NeedsTerminal ? privilege.ShellCommand(command) : null };
    }

    protected async Task<string> OutputAsync(string program, IEnumerable<string> arguments, CancellationToken cancellationToken, bool allowFailure = false)
    {
        var result = await Runner.RunAsync(program, arguments, new ProgramRunOptions { Timeout = TimeSpan.FromMinutes(2), Environment = new Dictionary<string, string?> { ["LC_ALL"] = "C" } }, cancellationToken).ConfigureAwait(false);
        if (!result.WasFound)
        {
            throw new InvalidOperationException($"{program} was not found.");
        }

        return result.Success || allowFailure ? result.StdOut : throw new InvalidOperationException(result.Message);
    }

    protected static IEnumerable<string> Lines(string text) => text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    protected static string[] Tabs(string line) => line.Split('\t');

    [GeneratedRegex(@"\s+")]
    protected static partial Regex Spaces();
}

/// <summary>Debian, Ubuntu and derivatives: dpkg-query, apt and apt-cache.</summary>
public sealed partial class AptPackageManager(IProgramRunner runner, IPrivilegeService privilege) : PackageManagerBase(runner, privilege)
{
    public override string Name => "apt";

    public override bool IsSupported => OperatingSystem.IsLinux() && Runner.Find("apt-get") is not null && Runner.Find("dpkg-query") is not null;

    protected override string Program => "apt-get";

    public override async Task<IReadOnlyList<SystemPackage>> ListInstalledAsync(CancellationToken cancellationToken = default) =>
        ParseInstalled(await OutputAsync("dpkg-query", ["-W", "-f=${Package}\t${Version}\t${db:Status-Abbrev}\t${binary:Summary}\n"], cancellationToken).ConfigureAwait(false));

    public override async Task<IReadOnlyList<SystemPackage>> ListUpgradesAsync(CancellationToken cancellationToken = default) =>
        ParseUpgradable(await OutputAsync("apt", ["list", "--upgradable"], cancellationToken, allowFailure: true).ConfigureAwait(false));

    public override async Task<IReadOnlyList<SystemPackage>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var found = ParseSearch(await OutputAsync("apt-cache", ["search", "--names-only", "--", query], cancellationToken, allowFailure: true).ConfigureAwait(false));
        var installed = (await ListInstalledAsync(cancellationToken).ConfigureAwait(false)).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        return [.. found.Take(300).Select(p => p with { Installed = installed.Contains(p.Name) })];
    }

    public override async Task<IReadOnlyList<string>> InfoAsync(string name, CancellationToken cancellationToken = default)
    {
        PackageNames.Require([name]);
        return [.. Lines(await OutputAsync("apt-cache", ["show", "--", name], cancellationToken, allowFailure: true).ConfigureAwait(false)).Take(40)];
    }

    protected override IReadOnlyList<string> Arguments(PackageAction action, IReadOnlyList<string> names) => action switch
    {
        PackageAction.Install => ["install", .. names],
        PackageAction.Remove => ["remove", .. names],
        PackageAction.Upgrade => ["install", "--only-upgrade", .. names],
        PackageAction.UpgradeAll => ["upgrade"],
        _ => ["update"],
    };

    public static IReadOnlyList<SystemPackage> ParseInstalled(string output) =>
        [.. Lines(output).Select(Tabs).Where(p => p.Length >= 3 && p[2].StartsWith("ii", StringComparison.Ordinal)).Select(p => new SystemPackage(p[0], p[1], p.Length > 3 ? p[3] : null))];

    // "openssl/jammy-updates,jammy-security 3.0.2-0ubuntu1.18 amd64 [upgradable from: 3.0.2-0ubuntu1.15]"
    public static IReadOnlyList<SystemPackage> ParseUpgradable(string output)
    {
        var list = new List<SystemPackage>();
        foreach (var line in Lines(output))
        {
            var match = UpgradableLine().Match(line);
            if (match.Success)
            {
                list.Add(new SystemPackage(match.Groups[1].Value, match.Groups[5].Value) { NewVersion = match.Groups[3].Value, Repository = match.Groups[2].Value });
            }
        }

        return list;
    }

    public static IReadOnlyList<SystemPackage> ParseSearch(string output) =>
        [.. Lines(output).Select(l => l.Split(" - ", 2)).Where(p => p[0].Length > 0 && PackageNames.IsValid(p[0])).Select(p => new SystemPackage(p[0], string.Empty, p.Length > 1 ? p[1] : null) { Installed = false })];

    [GeneratedRegex(@"^([^/\s]+)/(\S+)\s+(\S+)\s+(\S+)\s+\[upgradable from: ([^\]]+)\]")]
    private static partial Regex UpgradableLine();
}

/// <summary>Fedora, RHEL and derivatives: rpm for the installed list, dnf for the rest.</summary>
public sealed class DnfPackageManager(IProgramRunner runner, IPrivilegeService privilege) : PackageManagerBase(runner, privilege)
{
    public override string Name => "dnf";

    public override bool IsSupported => OperatingSystem.IsLinux() && Runner.Find("dnf") is not null;

    protected override string Program => "dnf";

    public override async Task<IReadOnlyList<SystemPackage>> ListInstalledAsync(CancellationToken cancellationToken = default) =>
        RpmInstalled.Parse(await OutputAsync("rpm", ["-qa", "--qf", "%{NAME}\t%{VERSION}-%{RELEASE}\t%{SUMMARY}\n"], cancellationToken).ConfigureAwait(false));

    // `dnf check-update` exits 100 when there are updates.
    public override async Task<IReadOnlyList<SystemPackage>> ListUpgradesAsync(CancellationToken cancellationToken = default) =>
        ParseCheckUpdate(await OutputAsync("dnf", ["-q", "check-update"], cancellationToken, allowFailure: true).ConfigureAwait(false));

    public override async Task<IReadOnlyList<SystemPackage>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var found = ParseSearch(await OutputAsync("dnf", ["-q", "search", "--", query], cancellationToken, allowFailure: true).ConfigureAwait(false));
        var installed = (await ListInstalledAsync(cancellationToken).ConfigureAwait(false)).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        return [.. found.Take(300).Select(p => p with { Installed = installed.Contains(p.Name) })];
    }

    public override async Task<IReadOnlyList<string>> InfoAsync(string name, CancellationToken cancellationToken = default)
    {
        PackageNames.Require([name]);
        return [.. Lines(await OutputAsync("dnf", ["-q", "info", "--", name], cancellationToken, allowFailure: true).ConfigureAwait(false)).Take(40)];
    }

    protected override IReadOnlyList<string> Arguments(PackageAction action, IReadOnlyList<string> names) => action switch
    {
        PackageAction.Install => ["install", .. names],
        PackageAction.Remove => ["remove", .. names],
        PackageAction.Upgrade => ["upgrade", .. names],
        PackageAction.UpgradeAll => ["upgrade"],
        _ => ["makecache"],
    };

    // "name.arch   1.2-3.fc40   updates"
    public static IReadOnlyList<SystemPackage> ParseCheckUpdate(string output)
    {
        var list = new List<SystemPackage>();
        foreach (var line in Lines(output))
        {
            if (line.StartsWith("Obsoleting", StringComparison.Ordinal))
            {
                break;
            }

            var parts = Spaces().Split(line.Trim());
            if (parts.Length >= 3 && parts[0].Contains('.', StringComparison.Ordinal))
            {
                list.Add(new SystemPackage(parts[0][..parts[0].LastIndexOf('.')], string.Empty) { NewVersion = parts[1], Repository = parts[2] });
            }
        }

        return list;
    }

    // "name.arch : Summary" lines under "=== Name Matched: x ===" headers
    public static IReadOnlyList<SystemPackage> ParseSearch(string output)
    {
        var list = new List<SystemPackage>();
        foreach (var line in Lines(output))
        {
            var parts = line.Split(" : ", 2);
            if (parts.Length == 2 && !line.StartsWith('=') && parts[0].Trim().Contains('.', StringComparison.Ordinal))
            {
                var name = parts[0].Trim();
                list.Add(new SystemPackage(name[..name.LastIndexOf('.')], string.Empty, parts[1].Trim()) { Installed = false });
            }
        }

        return list;
    }
}

/// <summary>openSUSE and SLES: rpm and zypper.</summary>
public sealed class ZypperPackageManager(IProgramRunner runner, IPrivilegeService privilege) : PackageManagerBase(runner, privilege)
{
    public override string Name => "zypper";

    public override bool IsSupported => OperatingSystem.IsLinux() && Runner.Find("zypper") is not null;

    protected override string Program => "zypper";

    public override async Task<IReadOnlyList<SystemPackage>> ListInstalledAsync(CancellationToken cancellationToken = default) =>
        RpmInstalled.Parse(await OutputAsync("rpm", ["-qa", "--qf", "%{NAME}\t%{VERSION}-%{RELEASE}\t%{SUMMARY}\n"], cancellationToken).ConfigureAwait(false));

    public override async Task<IReadOnlyList<SystemPackage>> ListUpgradesAsync(CancellationToken cancellationToken = default) =>
        ParseUpdates(await OutputAsync("zypper", ["--quiet", "--non-interactive", "list-updates"], cancellationToken, allowFailure: true).ConfigureAwait(false));

    public override async Task<IReadOnlyList<SystemPackage>> SearchAsync(string query, CancellationToken cancellationToken = default) =>
        ParseSearch(await OutputAsync("zypper", ["--quiet", "--non-interactive", "search", "--", query], cancellationToken, allowFailure: true).ConfigureAwait(false));

    public override async Task<IReadOnlyList<string>> InfoAsync(string name, CancellationToken cancellationToken = default)
    {
        PackageNames.Require([name]);
        return [.. Lines(await OutputAsync("zypper", ["--quiet", "--non-interactive", "info", "--", name], cancellationToken, allowFailure: true).ConfigureAwait(false)).Take(40)];
    }

    protected override IReadOnlyList<string> Arguments(PackageAction action, IReadOnlyList<string> names) => action switch
    {
        PackageAction.Install => ["install", .. names],
        PackageAction.Remove => ["remove", .. names],
        PackageAction.Upgrade => ["update", .. names],
        PackageAction.UpgradeAll => ["update"],
        _ => ["refresh"],
    };

    // "v | Repository | Name | Current Version | Available Version | Arch"
    public static IReadOnlyList<SystemPackage> ParseUpdates(string output) =>
        [.. Lines(output).Select(l => l.Split('|', StringSplitOptions.TrimEntries)).Where(p => p.Length >= 6 && p[0] == "v")
            .Select(p => new SystemPackage(p[2], p[3]) { NewVersion = p[4], Repository = p[1] })];

    // "S | Name | Summary | Type"
    public static IReadOnlyList<SystemPackage> ParseSearch(string output) =>
        [.. Lines(output).Select(l => l.Split('|', StringSplitOptions.TrimEntries)).Where(p => p.Length >= 4 && p[1] != "Name" && PackageNames.IsValid(p[1]))
            .Select(p => new SystemPackage(p[1], string.Empty, p[2]) { Installed = p[0].StartsWith('i') })];
}

/// <summary>Arch and derivatives: pacman.</summary>
public sealed class PacmanPackageManager(IProgramRunner runner, IPrivilegeService privilege) : PackageManagerBase(runner, privilege)
{
    public override string Name => "pacman";

    public override bool IsSupported => OperatingSystem.IsLinux() && Runner.Find("pacman") is not null;

    protected override string Program => "pacman";

    public override async Task<IReadOnlyList<SystemPackage>> ListInstalledAsync(CancellationToken cancellationToken = default) =>
        ParseInstalled(await OutputAsync("pacman", ["-Q"], cancellationToken).ConfigureAwait(false));

    public override async Task<IReadOnlyList<SystemPackage>> ListUpgradesAsync(CancellationToken cancellationToken = default) =>
        ParseUpgrades(await OutputAsync("pacman", ["-Qu"], cancellationToken, allowFailure: true).ConfigureAwait(false));

    public override async Task<IReadOnlyList<SystemPackage>> SearchAsync(string query, CancellationToken cancellationToken = default) =>
        ParseSearch(await OutputAsync("pacman", ["-Ss", "--", query], cancellationToken, allowFailure: true).ConfigureAwait(false));

    public override async Task<IReadOnlyList<string>> InfoAsync(string name, CancellationToken cancellationToken = default)
    {
        PackageNames.Require([name]);
        return [.. Lines(await OutputAsync("pacman", ["-Si", "--", name], cancellationToken, allowFailure: true).ConfigureAwait(false)).Take(40)];
    }

    protected override IReadOnlyList<string> Arguments(PackageAction action, IReadOnlyList<string> names) => action switch
    {
        PackageAction.Install => ["-S", .. names],
        PackageAction.Remove => ["-R", .. names],
        PackageAction.Upgrade => ["-S", .. names],
        PackageAction.UpgradeAll => ["-Syu"],
        _ => ["-Sy"],
    };

    public static IReadOnlyList<SystemPackage> ParseInstalled(string output) =>
        [.. Lines(output).Select(l => l.Split(' ', 2)).Where(p => p.Length == 2).Select(p => new SystemPackage(p[0], p[1]))];

    // "linux 6.9.1.arch1-1 -> 6.9.2.arch1-1"
    public static IReadOnlyList<SystemPackage> ParseUpgrades(string output) =>
        [.. Lines(output).Select(l => Spaces().Split(l.Trim())).Where(p => p.Length >= 4 && p[2] == "->").Select(p => new SystemPackage(p[0], p[1]) { NewVersion = p[3] })];

    // "extra/vim 9.1 [installed]\n    Vi Improved"
    public static IReadOnlyList<SystemPackage> ParseSearch(string output)
    {
        var list = new List<SystemPackage>();
        var lines = Lines(output).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].StartsWith(' ') || lines[i].IndexOf('/') is var slash and < 1)
            {
                continue;
            }

            var parts = Spaces().Split(lines[i].Trim());
            var name = parts[0][(parts[0].IndexOf('/') + 1)..];
            if (!PackageNames.IsValid(name))
            {
                continue;
            }

            list.Add(new SystemPackage(name, parts.Length > 1 ? parts[1] : string.Empty, i + 1 < lines.Count && lines[i + 1].StartsWith(' ') ? lines[i + 1].Trim() : null)
            {
                Installed = lines[i].Contains("[installed", StringComparison.Ordinal),
                Repository = parts[0][..parts[0].IndexOf('/')],
            });
        }

        return list;
    }
}

/// <summary>Homebrew on macOS (and Linux): runs as the user, no privileges.</summary>
public sealed class BrewPackageManager(IProgramRunner runner, IPrivilegeService privilege) : PackageManagerBase(runner, privilege)
{
    public override string Name => "Homebrew";

    public override bool IsSupported => Runner.Find("brew") is not null;

    public override bool NeedsPrivileges => false;

    protected override string Program => "brew";

    public override async Task<IReadOnlyList<SystemPackage>> ListInstalledAsync(CancellationToken cancellationToken = default) =>
        ParseInstalled(await OutputAsync("brew", ["list", "--versions"], cancellationToken).ConfigureAwait(false));

    public override async Task<IReadOnlyList<SystemPackage>> ListUpgradesAsync(CancellationToken cancellationToken = default) =>
        ParseOutdated(await OutputAsync("brew", ["outdated", "--verbose"], cancellationToken, allowFailure: true).ConfigureAwait(false));

    public override async Task<IReadOnlyList<SystemPackage>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var names = Lines(await OutputAsync("brew", ["search", query], cancellationToken, allowFailure: true).ConfigureAwait(false)).Where(l => !l.StartsWith("==>", StringComparison.Ordinal) && PackageNames.IsValid(l.Trim())).Select(l => l.Trim());
        var installed = (await ListInstalledAsync(cancellationToken).ConfigureAwait(false)).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        return [.. names.Take(300).Select(n => new SystemPackage(n, string.Empty) { Installed = installed.Contains(n) })];
    }

    public override async Task<IReadOnlyList<string>> InfoAsync(string name, CancellationToken cancellationToken = default)
    {
        PackageNames.Require([name]);
        return [.. Lines(await OutputAsync("brew", ["info", name], cancellationToken, allowFailure: true).ConfigureAwait(false)).Take(40)];
    }

    protected override IReadOnlyList<string> Arguments(PackageAction action, IReadOnlyList<string> names) => action switch
    {
        PackageAction.Install => ["install", .. names],
        PackageAction.Remove => ["uninstall", .. names],
        PackageAction.Upgrade => ["upgrade", .. names],
        PackageAction.UpgradeAll => ["upgrade"],
        _ => ["update"],
    };

    // "git 2.45.1 2.45.0"
    public static IReadOnlyList<SystemPackage> ParseInstalled(string output) =>
        [.. Lines(output).Select(l => Spaces().Split(l.Trim())).Where(p => p.Length >= 2).Select(p => new SystemPackage(p[0], p[^1]))];

    // "node (21.0.0) < 22.1.0"
    public static IReadOnlyList<SystemPackage> ParseOutdated(string output) =>
        [.. Lines(output).Select(l => Spaces().Split(l.Trim())).Where(p => p.Length >= 4 && p[2] == "<")
            .Select(p => new SystemPackage(p[0], p[1].Trim('(', ')')) { NewVersion = p[3] })];
}

internal static class RpmInstalled
{
    // name<TAB>version-release<TAB>summary
    public static IReadOnlyList<SystemPackage> Parse(string output) =>
        [.. output.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\t')).Where(p => p.Length >= 2 && p[0] != "gpg-pubkey")
            .Select(p => new SystemPackage(p[0], p[1], p.Length > 2 ? p[2] : null))];
}

/// <summary>Picks the package manager of this machine.</summary>
public static class PackageManagers
{
    public static ISystemPackageManager Detect(IProgramRunner runner, IPrivilegeService privilege)
    {
        ISystemPackageManager[] candidates = OperatingSystem.IsMacOS()
            ? [new BrewPackageManager(runner, privilege)]
            : [new AptPackageManager(runner, privilege), new DnfPackageManager(runner, privilege), new PacmanPackageManager(runner, privilege), new ZypperPackageManager(runner, privilege), new BrewPackageManager(runner, privilege)];
        return candidates.FirstOrDefault(c => c.IsSupported) ?? candidates[0];
    }
}
