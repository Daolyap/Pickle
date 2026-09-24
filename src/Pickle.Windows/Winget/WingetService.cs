using System.Management.Automation;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Windows.Processes;

namespace Pickle.Windows.Winget;

/// <summary>
/// winget via the Microsoft.WinGet.Client PowerShell module (structured objects, preferred) or by parsing winget.exe
/// output. Machine-scope installs and elevated source repair go through <see cref="IElevationBroker"/>.
/// </summary>
public sealed class WingetService : IWingetService
{
    public const string SourceMsixUrl = "https://cdn.winget.microsoft.com/cache/source.msix";

    internal static readonly string[] CommonFlags = ["--accept-source-agreements", "--disable-interactivity"];

    private static readonly TimeSpan QueryTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(60);

    private const string ListScript = """
        param($upgradesOnly, $includeUnknown)
        Import-Module Microsoft.WinGet.Client -ErrorAction Stop
        Get-WinGetPackage -ErrorAction Stop |
            Where-Object { -not $upgradesOnly -or ($_.IsUpdateAvailable -and ($includeUnknown -or [string]$_.InstalledVersion -ne 'Unknown')) } |
            ForEach-Object {
                [pscustomobject]@{
                    Id = [string]$_.Id
                    Name = [string]$_.Name
                    InstalledVersion = [string]$_.InstalledVersion
                    Available = if ($_.IsUpdateAvailable) { [string]@($_.AvailableVersions)[0] } else { '' }
                    Source = [string]$_.Source
                }
            }
        """;

    private const string SearchScript = """
        param($query)
        Import-Module Microsoft.WinGet.Client -ErrorAction Stop
        Find-WinGetPackage -Query $query -ErrorAction Stop | ForEach-Object {
            [pscustomobject]@{ Id = [string]$_.Id; Name = [string]$_.Name; Version = [string]$_.Version; Source = [string]$_.Source }
        }
        """;

    private const string DetailsScript = """
        param($id)
        Import-Module Microsoft.WinGet.Client -ErrorAction Stop
        Find-WinGetPackage -Id $id -MatchOption Equals -ErrorAction Stop | Select-Object -First 1 | ForEach-Object {
            [pscustomobject]@{ Id = [string]$_.Id; Name = [string]$_.Name; Version = [string]$_.Version; Versions = @($_.AvailableVersions | ForEach-Object { [string]$_ }) }
        }
        """;

    private const string InstallScript = """
        param($verb, $id, $version, $scope, $force, $includeUnknown)
        Import-Module Microsoft.WinGet.Client -ErrorAction Stop
        $p = @{ Id = $id; MatchOption = 'Equals'; Mode = 'Silent'; ErrorAction = 'Stop' }
        if ($version) { $p.Version = $version }
        if ($scope) { $p.Scope = $scope }
        if ($force) { $p.Force = $true }
        if ($includeUnknown) { $p.IncludeUnknown = $true }
        $result = switch ($verb) {
            'install' { Install-WinGetPackage @p }
            'upgrade' { Update-WinGetPackage @p }
            'uninstall' { $p.Remove('Version'); $p.Remove('Scope'); $p.Remove('IncludeUnknown'); Uninstall-WinGetPackage @p }
        }
        $result | ForEach-Object {
            [pscustomobject]@{ Status = [string]$_.Status; RebootRequired = [bool]$_.RebootRequired; ExtendedErrorCode = [string]$_.ExtendedErrorCode }
        }
        """;

    private const string SourcesScript = """
        Import-Module Microsoft.WinGet.Client -ErrorAction Stop
        Get-WinGetSource -ErrorAction Stop | ForEach-Object {
            [pscustomobject]@{ Name = [string]$_.Name; Argument = [string]$_.Argument; Type = [string]$_.Type }
        }
        """;

    private const string InstallModuleScript = """
        if (Get-Command Install-PSResource -ErrorAction SilentlyContinue) {
            try {
                Install-PSResource -Name Microsoft.WinGet.Client -Scope CurrentUser -TrustRepository -Quiet -ErrorAction Stop
                'Install-PSResource'
                return
            } catch {
                Write-Warning "Install-PSResource failed: $_"
            }
        }
        Install-Module -Name Microsoft.WinGet.Client -Scope CurrentUser -Force -AllowClobber -ErrorAction Stop
        'Install-Module'
        """;

    // Only when Windows PowerShell is missing: PowerShell 7 can load Appx natively on current Windows builds.
    private const string InProcessRepairScript = """
        Import-Module Appx -ErrorAction Stop
        Add-AppxPackage -Path 'https://cdn.winget.microsoft.com/cache/source.msix' -ForceApplicationShutdown -ErrorAction Stop
        """;

    private readonly IPickleShell _shell;
    private readonly Func<IElevationBroker?> _broker;
    private readonly IPickleLogger _log;
    private readonly IProcessRunner _runner;
    private readonly Func<string?> _locateExe;
    private readonly Func<string?> _locateWindowsPowerShell;
    private readonly object _gate = new();
    private Task<WingetBackend>? _backend;

    public WingetService(IPickleContext context)
        : this(context.Shell, () => context.Services.Get<IElevationBroker>(), context.Log, new ProcessRunner(context.Log), LocateWingetExe, OperatingSystem.IsWindows())
    {
    }

    internal WingetService(
        IPickleShell shell,
        Func<IElevationBroker?> broker,
        IPickleLogger log,
        IProcessRunner runner,
        Func<string?> locateExe,
        bool isSupported,
        Func<string?>? locateWindowsPowerShell = null)
    {
        _shell = shell;
        _broker = broker;
        _log = log;
        _runner = runner;
        _locateExe = locateExe;
        _locateWindowsPowerShell = locateWindowsPowerShell ?? (() => File.Exists(WindowsPowerShellPath) ? WindowsPowerShellPath : null);
        IsSupported = isSupported;
    }

    public bool IsSupported { get; }

    public Task<WingetBackend> GetBackendAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
        {
            return Task.FromResult(WingetBackend.Unavailable);
        }

        lock (_gate)
        {
            if (_backend is null || _backend.IsFaulted || _backend.IsCanceled)
            {
                _backend = DetectBackendAsync();
            }

            return _backend.WaitAsync(cancellationToken);
        }
    }

    public async Task<WingetOperationResult> InstallClientModuleAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
        {
            return Unsupported();
        }

        _log.Info("winget", "installing Microsoft.WinGet.Client for the current user");
        var result = await _shell.InvokeAsync(InstallModuleScript, null, ShellTarget.Background, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _backend = null;
        }

        if (result.HadErrors)
        {
            var message = string.Join("; ", result.Errors.Select(e => e.ToString()));
            _log.Warn("winget", "module install failed: " + message);
            return new WingetOperationResult(false, "Installing Microsoft.WinGet.Client failed: " + message, 1);
        }

        var via = result.Output.LastOrDefault()?.ToString() ?? "PowerShellGet";
        return new WingetOperationResult(true, $"Microsoft.WinGet.Client installed for the current user ({via}).", 0);
    }

    public Task<IReadOnlyList<WingetPackage>> ListInstalledAsync(CancellationToken cancellationToken = default) =>
        QueryAsync(
            ct => RunModuleListAsync(false, false, ct),
            async ct => WingetCliParser.ParsePackages(await RunCliTextAsync(["list", .. CommonFlags], ct).ConfigureAwait(false), WingetTableKind.List),
            cancellationToken);

    public Task<IReadOnlyList<WingetPackage>> ListUpgradesAsync(bool includeUnknown = false, CancellationToken cancellationToken = default) =>
        QueryAsync(
            ct => RunModuleListAsync(true, includeUnknown, ct),
            async ct =>
            {
                string[] args = includeUnknown ? ["upgrade", "--include-unknown", .. CommonFlags] : ["upgrade", .. CommonFlags];
                var packages = WingetCliParser.ParsePackages(await RunCliTextAsync(args, ct).ConfigureAwait(false), WingetTableKind.Upgrade);
                return [.. packages.Where(p => p.IsUpgradable || !string.IsNullOrEmpty(p.AvailableVersion))];
            },
            cancellationToken);

    public Task<IReadOnlyList<WingetPackage>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var q = WindowsIds.RequireSearchQuery(query);
        return QueryAsync(
            async ct =>
            {
                var output = await InvokeModuleAsync(SearchScript, new Dictionary<string, object?> { ["query"] = q }, ct).ConfigureAwait(false);
                return [.. output.Select(o => new WingetPackage(Str(o, "Id"), Str(o, "Name"), null, NullIfEmpty(Str(o, "Version")), NullIfEmpty(Str(o, "Source"))))];
            },
            async ct => WingetCliParser.ParsePackages(await RunCliTextAsync(["search", "--query", q, .. CommonFlags], ct).ConfigureAwait(false), WingetTableKind.Search),
            cancellationToken);
    }

    public async Task<WingetPackageDetails?> GetDetailsAsync(string id, CancellationToken cancellationToken = default)
    {
        WindowsIds.RequireWingetId(id);
        if (!IsSupported)
        {
            return null;
        }

        if (_locateExe() is not null)
        {
            var show = await RunCliTextAsync(["show", "--id", id, "--exact", .. CommonFlags], cancellationToken).ConfigureAwait(false);
            IReadOnlyList<string> versions = [];
            try
            {
                versions = WingetCliParser.ParseVersions(
                    await RunCliTextAsync(["show", "--id", id, "--exact", "--versions", .. CommonFlags], cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                _log.Warn("winget", $"listing versions of {id} failed", ex);
            }

            return WingetCliParser.ParseShow(show, versions);
        }

        if (await GetBackendAsync(cancellationToken).ConfigureAwait(false) != WingetBackend.PowerShellModule)
        {
            return null;
        }

        var output = await InvokeModuleAsync(DetailsScript, new Dictionary<string, object?> { ["id"] = id }, cancellationToken).ConfigureAwait(false);
        if (output.FirstOrDefault() is not { } o)
        {
            return null;
        }

        var all = o.Properties["Versions"]?.Value is object[] list ? list.Select(v => v?.ToString() ?? string.Empty).Where(v => v.Length > 0).ToList() : [];
        var latest = NullIfEmpty(Str(o, "Version")) ?? all.FirstOrDefault();
        return new WingetPackageDetails(Str(o, "Id"), Str(o, "Name"), null, null, null, null, latest, all);
    }

    public async Task<WingetOperationResult> InstallAsync(string id, WingetInstallOptions options, IProgress<WingetProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        WindowsIds.RequireWingetId(id);
        if (options.Version is not null)
        {
            WindowsIds.RequireWingetVersion(options.Version);
        }

        if (!IsSupported)
        {
            return Unsupported();
        }

        if (options.Scope == WingetScope.Machine && options.Version is null && _broker() is { IsSupported: true, IsElevated: false } broker)
        {
            return await ViaBrokerAsync(broker, ElevatedOperationKind.WingetInstall, [id], "install", id, progress, cancellationToken).ConfigureAwait(false);
        }

        return await ChangeAsync("install", id, options, progress, cancellationToken).ConfigureAwait(false);
    }

    public Task<WingetOperationResult> UpgradeAsync(string id, WingetInstallOptions options, IProgress<WingetProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        WindowsIds.RequireWingetId(id);
        if (options.Version is not null)
        {
            WindowsIds.RequireWingetVersion(options.Version);
        }

        return IsSupported ? ChangeAsync("upgrade", id, options, progress, cancellationToken) : Task.FromResult(Unsupported());
    }

    public Task<WingetOperationResult> UninstallAsync(string id, IProgress<WingetProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        WindowsIds.RequireWingetId(id);
        return IsSupported ? ChangeAsync("uninstall", id, new WingetInstallOptions(), progress, cancellationToken) : Task.FromResult(Unsupported());
    }

    public async Task<WingetOperationResult> UninstallElevatedAsync(IReadOnlyList<string> ids, IProgress<WingetProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var valid = ids.Select(WindowsIds.RequireWingetId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (valid.Count == 0)
        {
            return new WingetOperationResult(true, "Nothing to uninstall.", 0);
        }

        if (!IsSupported)
        {
            return Unsupported();
        }

        if (_broker() is not { IsSupported: true } broker)
        {
            return new WingetOperationResult(false, "Elevation is not available in this session.", 1);
        }

        return await ViaBrokerAsync(broker, ElevatedOperationKind.WingetUninstall, valid, "uninstall", null, progress, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<WingetSource>> ListSourcesAsync(CancellationToken cancellationToken = default) =>
        QueryAsync(
            async ct =>
            {
                var output = await InvokeModuleAsync(SourcesScript, null, ct).ConfigureAwait(false);
                return [.. output.Select(o => new WingetSource(Str(o, "Name"), Str(o, "Argument"), Str(o, "Type")))];
            },
            async ct =>
            {
                var sources = WingetCliParser.ParseSources(await RunCliTextAsync(["source", "export"], ct).ConfigureAwait(false));
                return sources.Count > 0 ? sources : WingetCliParser.ParseSources(await RunCliTextAsync(["source", "list"], ct).ConfigureAwait(false));
            },
            cancellationToken);

    public async Task<WingetOperationResult> RepairSourceAsync(bool elevated, CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
        {
            return Unsupported();
        }

        if (elevated)
        {
            if (_broker() is not { IsSupported: true } broker)
            {
                return new WingetOperationResult(false, "Elevation is not available in this session.", 1);
            }

            return await ViaBrokerAsync(broker, ElevatedOperationKind.WingetRepairSource, [], "repair-source", null, null, cancellationToken).ConfigureAwait(false);
        }

        _log.Info("winget", "repairing the winget source (current user)");
        if (_locateWindowsPowerShell() is { } powershell)
        {
            var result = await _runner.RunAsync(
                powershell,
                WingetSourceRepair.Arguments(),
                null,
                QueryTimeout,
                cancellationToken,
                WingetSourceRepair.Environment()).ConfigureAwait(false);
            var outcome = WingetSourceRepair.Interpret(result.ExitCode, result.Output, result.TimedOut, "for the current user");
            _log.Info("winget", $"source repair: success={outcome.Success} code={outcome.ExitCode}: {outcome.Message}");
            return outcome;
        }

        _log.Warn("winget", "Windows PowerShell not found; repairing the source in-process");
        var inProcess = await _shell.InvokeAsync(InProcessRepairScript, null, ShellTarget.Background, cancellationToken).ConfigureAwait(false);
        if (!inProcess.HadErrors)
        {
            return new WingetOperationResult(true, "The winget source package was re-registered for the current user.", 0);
        }

        var errors = string.Join(Environment.NewLine, inProcess.Errors.Select(e => e.ToString()));
        return WingetSourceRepair.Interpret(1, errors, false, "for the current user");
    }

    internal static string WindowsPowerShellPath => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>winget.exe on PATH, else the per-user App Installer execution alias.</summary>
    internal static string? LocateWingetExe()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), "winget.exe");
                if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local))
        {
            return null;
        }

        var alias = Path.Combine(local, "Microsoft", "WindowsApps", "winget.exe");
        return File.Exists(alias) ? alias : null;
    }

    internal static IReadOnlyList<string> BuildChangeArguments(string verb, string id, WingetInstallOptions options)
    {
        var args = new List<string> { verb, "--id", id, "--exact", "--silent" };
        if (verb != "uninstall")
        {
            args.Add("--accept-package-agreements");
            if (options.Version is not null)
            {
                args.AddRange(["--version", options.Version]);
            }

            if (verb == "install" && options.Scope != WingetScope.Any)
            {
                args.AddRange(["--scope", options.Scope == WingetScope.Machine ? "machine" : "user"]);
            }

            if (options.Force)
            {
                args.Add("--force");
            }

            if (verb == "upgrade" && options.IncludeUnknown)
            {
                args.Add("--include-unknown");
            }
        }

        args.AddRange(CommonFlags);
        return args;
    }

    private async Task<WingetBackend> DetectBackendAsync()
    {
        try
        {
            var result = await _shell.InvokeAsync(
                "[bool](Get-Module -ListAvailable -Name Microsoft.WinGet.Client)", null, ShellTarget.Background).ConfigureAwait(false);
            if (!result.HadErrors && result.Output.FirstOrDefault()?.BaseObject is true)
            {
                _log.Info("winget", "backend: Microsoft.WinGet.Client module");
                return WingetBackend.PowerShellModule;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn("winget", "module detection failed", ex);
        }

        var exe = _locateExe();
        _log.Info("winget", exe is null ? "backend: unavailable" : "backend: winget.exe at " + exe);
        return exe is null ? WingetBackend.Unavailable : WingetBackend.Cli;
    }

    private async Task<IReadOnlyList<T>> QueryAsync<T>(
        Func<CancellationToken, Task<IReadOnlyList<T>>> module,
        Func<CancellationToken, Task<IReadOnlyList<T>>> cli,
        CancellationToken cancellationToken)
    {
        if (!IsSupported)
        {
            return [];
        }

        var backend = await GetBackendAsync(cancellationToken).ConfigureAwait(false);
        if (backend == WingetBackend.PowerShellModule)
        {
            try
            {
                return await module(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or RuntimeException && _locateExe() is not null)
            {
                _log.Warn("winget", "module query failed, falling back to winget.exe", ex);
            }
        }
        else if (backend == WingetBackend.Unavailable)
        {
            throw new InvalidOperationException(NotFoundMessage);
        }

        return await cli(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<WingetPackage>> RunModuleListAsync(bool upgradesOnly, bool includeUnknown, CancellationToken cancellationToken)
    {
        var output = await InvokeModuleAsync(
            ListScript,
            new Dictionary<string, object?> { ["upgradesOnly"] = upgradesOnly, ["includeUnknown"] = includeUnknown },
            cancellationToken).ConfigureAwait(false);
        return [.. output.Select(o => new WingetPackage(
            Str(o, "Id"),
            Str(o, "Name"),
            NullIfEmpty(Str(o, "InstalledVersion")),
            NullIfEmpty(Str(o, "Available")),
            NullIfEmpty(Str(o, "Source"))))];
    }

    private async Task<WingetOperationResult> ChangeAsync(string verb, string id, WingetInstallOptions options, IProgress<WingetProgress>? progress, CancellationToken cancellationToken)
    {
        var backend = await GetBackendAsync(cancellationToken).ConfigureAwait(false);
        _log.Info("winget", $"{verb} {id} via {backend} (version={options.Version}, scope={options.Scope})");
        switch (backend)
        {
            case WingetBackend.PowerShellModule:
                progress?.Report(new WingetProgress(verb == "uninstall" ? "Uninstalling" : "Installing", null, $"{verb} {id}"));
                var parameters = new Dictionary<string, object?>
                {
                    ["verb"] = verb,
                    ["id"] = id,
                    ["version"] = options.Version,
                    ["scope"] = verb == "install" ? options.Scope switch { WingetScope.User => "User", WingetScope.Machine => "System", _ => null } : null,
                    ["force"] = options.Force,
                    ["includeUnknown"] = verb == "upgrade" && options.IncludeUnknown,
                };
                var result = await _shell.InvokeAsync(InstallScript, parameters, ShellTarget.Background, cancellationToken).ConfigureAwait(false);
                if (result.HadErrors)
                {
                    var error = string.Join("; ", result.Errors.Select(e => e.ToString()));
                    progress?.Report(new WingetProgress("Failed", null, error));
                    return new WingetOperationResult(false, $"{verb} {id}: {error}", 1)
                    {
                        Output = string.Join(Environment.NewLine, result.Errors.Select(e => e.ToString())),
                    };
                }

                var status = result.Output.LastOrDefault();
                var mapped = WingetErrors.FromModuleStatus(
                    status is null ? null : Str(status, "Status"),
                    status?.Properties["RebootRequired"]?.Value is true,
                    verb,
                    id,
                    status is null ? null : Str(status, "ExtendedErrorCode"));
                progress?.Report(new WingetProgress(mapped.Success ? "Done" : "Failed", mapped.Success ? 100 : null, mapped.Message));
                return mapped;

            case WingetBackend.Cli:
                var tracker = new WingetProgressTracker(progress);
                var run = await RunCliAsync(BuildChangeArguments(verb, id, options), tracker.Feed, InstallTimeout, cancellationToken).ConfigureAwait(false);
                if (run.TimedOut)
                {
                    return new WingetOperationResult(false, $"{verb} {id}: timed out.", -1) { Output = WingetCliParser.Transcript(run.Output) };
                }

                var outcome = WingetErrors.FromExitCode(run.ExitCode, run.Output, verb, id) with { Output = WingetCliParser.Transcript(run.Output) };
                progress?.Report(new WingetProgress(outcome.Success ? "Done" : "Failed", outcome.Success ? 100 : null, outcome.Message));
                return outcome;

            default:
                return new WingetOperationResult(false, NotFoundMessage, 1);
        }
    }

    /// <param name="id">Prefix for the message ("install 7zip.7zip: …"); null when the helper's message already names the packages.</param>
    private async Task<WingetOperationResult> ViaBrokerAsync(
        IElevationBroker broker,
        ElevatedOperationKind kind,
        IReadOnlyList<string> arguments,
        string verb,
        string? id,
        IProgress<WingetProgress>? progress,
        CancellationToken cancellationToken)
    {
        var what = id ?? (arguments.Count == 0 ? "winget" : string.Join(", ", arguments));
        _log.Info("winget", $"{verb} {what} via the elevation broker");
        progress?.Report(new WingetProgress("Elevating", null, "Waiting for the administrator (UAC) prompt…"));
        try
        {
            var responses = await broker.RunAsync(
                [new ElevatedRequest(kind, arguments)],
                new SyncProgress<string>(m => progress?.Report(new WingetProgress("Elevated", null, m))),
                cancellationToken).ConfigureAwait(false);
            var response = responses.FirstOrDefault() ?? new ElevatedResponse(false, "The elevated helper returned no result.", 1);
            progress?.Report(new WingetProgress(response.Success ? "Done" : "Failed", response.Success ? 100 : null, response.Message));
            return new WingetOperationResult(response.Success, id is null ? response.Message : $"{verb} {id}: {response.Message}", response.ExitCode)
            {
                Output = response.Output,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new WingetOperationResult(false, $"{verb} {what}: the administrator (UAC) prompt was declined.", 1223);
        }
    }

    private async Task<IReadOnlyList<PSObject>> InvokeModuleAsync(string script, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        var result = await _shell.InvokeAsync(script, parameters, ShellTarget.Background, cancellationToken).ConfigureAwait(false);
        if (result.HadErrors)
        {
            throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => e.ToString())));
        }

        return [.. result.Output.Where(o => o is not null)];
    }

    private async Task<string> RunCliTextAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var result = await RunCliAsync(args, null, QueryTimeout, cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
        {
            throw new InvalidOperationException($"winget {args[0]} timed out.");
        }

        return result.Output;
    }

    private Task<ProcessResult> RunCliAsync(IReadOnlyList<string> args, Action<string>? onSegment, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var exe = _locateExe() ?? throw new InvalidOperationException(NotFoundMessage);
        return _runner.RunAsync(exe, args, onSegment, timeout, cancellationToken);
    }

    private const string NotFoundMessage =
        "winget is not available. Install 'App Installer' from the Microsoft Store (or run 'pk winget install-module').";

    private static WingetOperationResult Unsupported() => new(false, "winget is only available on Windows.", 1);

    private static string Str(PSObject o, string name) => o.Properties[name]?.Value?.ToString() ?? string.Empty;

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
