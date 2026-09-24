using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Windows.Tools;

/// <summary>
/// <see cref="IToolInstaller"/> on winget. After installing it makes the command reachable: the session's PATH picks up
/// what the installer added to the registry PATH, and when the program still isn't on PATH (7-Zip, OpenSSL, …) its
/// folder is found (known install folders, winget's portable package folders) and added — to the session always, and to
/// the user's PATH when <see cref="ToolInstallOptions.AddToPath"/> is set and the install isn't temporary.
/// </summary>
public sealed class WingetToolInstaller : IToolInstaller
{
    // winget install of an installed package: "No applicable upgrade found" / "already installed".
    private static readonly int[] AlreadyInstalledCodes = [unchecked((int)0x8A15002B), unchecked((int)0x8A15010D), unchecked((int)0x8A15010E)];

    private readonly Func<IWingetService?> _winget;
    private readonly Func<string, string?> _wizardWingetId;
    private readonly IPathStore _path;
    private readonly string _temporaryRoot;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, bool> _directoryExists;
    private readonly Func<string, string, int, IEnumerable<string>> _findFiles;
    private readonly List<(ToolPackage Package, string Location)> _temporary = [];
    private readonly object _gate = new();

    public WingetToolInstaller(
        Func<IWingetService?> winget,
        Func<string, string?> wizardWingetId,
        IPathStore path,
        string temporaryRoot,
        Func<string, bool>? fileExists = null,
        Func<string, bool>? directoryExists = null,
        Func<string, string, int, IEnumerable<string>>? findFiles = null)
    {
        _winget = winget;
        _wizardWingetId = wizardWingetId;
        _path = path;
        _temporaryRoot = temporaryRoot;
        _fileExists = fileExists ?? File.Exists;
        _directoryExists = directoryExists ?? Directory.Exists;
        _findFiles = findFiles ?? FindFiles;
    }

    public bool IsSupported => _winget() is { IsSupported: true };

    public IReadOnlyList<ToolPackage> TemporaryInstalls
    {
        get
        {
            lock (_gate)
            {
                return [.. _temporary.Select(t => t.Package)];
            }
        }
    }

    public ToolPackage? Find(string command)
    {
        var name = ToolCatalog.NormalizeCommand(command);
        if (name.Length == 0)
        {
            return null;
        }

        if (ToolCatalog.Find(name) is { } known)
        {
            return known;
        }

        return _wizardWingetId(name) is { Length: > 0 } id ? new ToolPackage(name, id, name) : null;
    }

    public async Task<ToolInstallResult> InstallAsync(ToolPackage package, ToolInstallOptions options, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (_winget() is not { IsSupported: true } winget)
        {
            return new ToolInstallResult(false, "winget isn't available on this system.");
        }

        string? location = null;
        if (options.Scope == ToolInstallScope.Temporary)
        {
            location = Path.Combine(_temporaryRoot, SafeFolderName(package.WingetId));
        }

        var scope = options.Scope == ToolInstallScope.Machine ? WingetScope.Machine : WingetScope.User;
        progress?.Report($"Installing {package.Name} ({package.WingetId})…");
        var result = await winget.InstallAsync(
            package.WingetId,
            new WingetInstallOptions(Scope: scope, Location: location),
            new Progress<WingetProgress>(p => progress?.Report(Describe(p))),
            cancellationToken).ConfigureAwait(false);
        var already = !result.Success && result.ExitCode is { } code && AlreadyInstalledCodes.Contains(code);
        if (!result.Success && !already)
        {
            return new ToolInstallResult(false, result.Message) { Output = result.Output };
        }

        if (location is not null)
        {
            lock (_gate)
            {
                _temporary.Add((package, location));
            }
        }

        var (executable, added) = MakeReachable(package, options, location);
        var message = (already ? $"{package.Name} was already installed" : $"Installed {package.Name}")
            + (options.Scope == ToolInstallScope.Temporary ? " for this session (removed when Pickle exits)" : string.Empty)
            + (executable is null ? $", but '{package.Command}' wasn't found. Open a new terminal, or add its folder to PATH." : ".")
            + (added.Count > 0 ? $" Added to PATH: {string.Join(", ", added)}" + (options.AddToPath && options.Scope != ToolInstallScope.Temporary ? string.Empty : " (this session)") : string.Empty);
        return new ToolInstallResult(true, message, executable, added) { Output = result.Output };
    }

    public async Task<ToolInstallResult> RemoveTemporaryAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        List<(ToolPackage Package, string Location)> installs;
        lock (_gate)
        {
            installs = [.. _temporary];
            _temporary.Clear();
        }

        if (installs.Count == 0 || _winget() is not { IsSupported: true } winget)
        {
            return new ToolInstallResult(true, "No temporary tools to remove.");
        }

        var failures = new List<string>();
        foreach (var (package, location) in installs.DistinctBy(i => i.Package.WingetId, StringComparer.OrdinalIgnoreCase))
        {
            progress?.Report($"Removing temporary {package.Name}…");
            var result = await winget.UninstallAsync(package.WingetId, null, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                failures.Add($"{package.Name}: {result.Message}");
            }

            RemoveFromProcessPath(location);
            TryDelete(location);
        }

        return failures.Count == 0
            ? new ToolInstallResult(true, $"Removed {installs.Count} temporary tool(s).")
            : new ToolInstallResult(false, "Could not remove " + string.Join("; ", failures));
    }

    private (string? Executable, List<string> Added) MakeReachable(ToolPackage package, ToolInstallOptions options, string? location)
    {
        var added = new List<string>();

        // Installers add to the registry PATH, which this process doesn't see until it's merged in.
        foreach (var entry in _path.PersistentEntries())
        {
            AddToProcessPath(entry);
        }

        var executable = FindOnPath(package.Command);
        if (executable is not null)
        {
            return (executable, added);
        }

        foreach (var directory in CandidateDirectories(package, location))
        {
            var found = FindIn(directory, package.Command);
            if (found is null)
            {
                continue;
            }

            var folder = Path.GetDirectoryName(found)!;
            AddToProcessPath(folder);
            if (options.AddToPath && options.Scope != ToolInstallScope.Temporary && !InPersistentPath(folder))
            {
                _path.AppendToUserPath(folder);
            }

            added.Add(folder);
            return (found, added);
        }

        return (null, added);
    }

    private IEnumerable<string> CandidateDirectories(ToolPackage package, string? location)
    {
        if (location is not null)
        {
            yield return location;
        }

        foreach (var pattern in package.InstallDirs)
        {
            foreach (var directory in ExpandPattern(_path.Expand(pattern)))
            {
                yield return directory;
            }
        }

        // winget's portable packages: links in ...\WinGet\Links, files in ...\WinGet\Packages\<id>_<source>.
        foreach (var root in new[] { @"%LOCALAPPDATA%\Microsoft\WinGet", @"%ProgramFiles%\WinGet" })
        {
            var expanded = _path.Expand(root);
            yield return Path.Combine(expanded, "Links");
            foreach (var directory in ExpandPattern(Path.Combine(expanded, "Packages", package.WingetId + "_*")))
            {
                yield return directory;
            }
        }
    }

    /// <summary>A folder, or its subfolders matching a trailing <c>name*</c> segment.</summary>
    private IEnumerable<string> ExpandPattern(string pattern)
    {
        if (!pattern.EndsWith('*'))
        {
            return _directoryExists(pattern) ? [pattern] : [];
        }

        var parent = Path.GetDirectoryName(pattern);
        if (parent is null || !_directoryExists(parent))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateDirectories(parent, Path.GetFileName(pattern)).OrderDescending(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private string? FindIn(string directory, string command)
    {
        foreach (var name in FileNames(command))
        {
            var direct = Path.Combine(directory, name);
            if (_fileExists(direct))
            {
                return direct;
            }
        }

        // Portable archives often unpack into a versioned subfolder (ffmpeg-7.1-full_build\bin\ffmpeg.exe).
        return FileNames(command).SelectMany(name => _findFiles(directory, name, 3)).FirstOrDefault();
    }

    private string? FindOnPath(string command)
    {
        foreach (var entry in Split(_path.ProcessPath))
        {
            if (!Path.IsPathFullyQualified(entry))
            {
                continue;
            }

            foreach (var name in FileNames(command))
            {
                var candidate = Path.Combine(entry, name);
                if (_fileExists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> FileNames(string command) =>
        Path.HasExtension(command) ? [command] : [command + ".exe", command + ".cmd", command + ".bat", command];

    private bool InPersistentPath(string directory) =>
        _path.PersistentEntries().Any(e => SamePath(e, directory));

    private void AddToProcessPath(string directory)
    {
        var entries = Split(_path.ProcessPath).ToList();
        if (!entries.Any(e => SamePath(e, directory)))
        {
            entries.Add(directory);
            _path.ProcessPath = string.Join(Path.PathSeparator, entries);
        }
    }

    private void RemoveFromProcessPath(string root)
    {
        var entries = Split(_path.ProcessPath).ToList();
        var kept = entries.Where(e => !IsUnder(e, root)).ToList();
        if (kept.Count != entries.Count)
        {
            _path.ProcessPath = string.Join(Path.PathSeparator, kept);
        }
    }

    private static IEnumerable<string> Split(string path) =>
        path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(e => e.Trim('"'));

    private static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static bool IsUnder(string path, string root) =>
        SamePath(path, root) || path.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string SafeFolderName(string id) => string.Concat(id.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_'));

    private static string Describe(WingetProgress p) =>
        p.Percent is { } percent ? $"{p.Stage} {percent:0}%" : string.IsNullOrEmpty(p.Message) ? p.Stage : $"{p.Stage}: {p.Message}";

    private static IEnumerable<string> FindFiles(string directory, string name, int depth)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = depth, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
            return Directory.EnumerateFiles(directory, name, options).Take(1).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
