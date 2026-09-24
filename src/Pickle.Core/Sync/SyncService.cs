using System.Text.Json;
using Pickle.Abstractions;
using Pickle.Core.Commands;
using Pickle.Core.Config;
using Pickle.Core.Contracts;

namespace Pickle.Core.Sync;

/// <summary>A sync failure with a message meant for the user.</summary>
public sealed class SyncException(string message) : Exception(message);

/// <summary>
/// Settings sync (<c>pk sync</c>). The folder backend syncs with a directory (e.g. OneDrive\Pickle); the git backend
/// keeps a working clone in DataDir/sync-repo, merges there, commits "Pickle sync from &lt;machine&gt;" and pushes.
/// The merge itself is <see cref="SyncEngine"/>. Optional auto sync runs in the background at startup and (bounded)
/// at exit.
/// </summary>
public sealed class SyncService : ISyncService, IRuntimeComponent, IDisposable
{
    private readonly PickleRuntime _runtime;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _backgroundSync;

    public SyncService(PickleRuntime runtime) => _runtime = runtime;

    public string RepoDir => Path.Combine(_runtime.Paths.DataDir, "sync-repo");

    public TimeSpan ExitTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The startup auto sync, if one was started (tests await it).</summary>
    public Task? BackgroundSync => _backgroundSync;

    private SyncSettings Settings => _runtime.Config.Current.Sync;

    private bool Configured => Settings.Backend is "folder" or "git" && !string.IsNullOrWhiteSpace(Settings.Target);

    // ───────────── Lifecycle ─────────────

    public void Initialize()
    {
        _runtime.CommandRegistry.Register(new SyncCommand(_runtime, this));
        _runtime.HookRegistry.Register(HookKind.Exit, (_, _) =>
        {
            SyncOnExit();
            return ValueTask.CompletedTask;
        });
    }

    public void OnStarted()
    {
        if (!Settings.AutoSyncOnStart || !Configured || _runtime.Options.Command is not null || _runtime.Options.File is not null)
        {
            return;
        }

        var token = _shutdown.Token;
        _backgroundSync = Task.Run(
            async () =>
            {
                try
                {
                    var report = await RunReportAsync(SyncDirection.Both, background: true, token).ConfigureAwait(false);
                    _runtime.Log.Info("sync", $"Startup sync: {report.Message} {string.Join("; ", report.Changes)}");
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _runtime.Log.Warn("sync", $"Startup sync failed: {ex.Message}", ex);
                }
            },
            token);
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        try
        {
            _backgroundSync?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _shutdown.Dispose();
    }

    private void SyncOnExit()
    {
        if (!Settings.AutoSyncOnExit || !Configured)
        {
            return;
        }

        using var cts = new CancellationTokenSource(ExitTimeout);
        try
        {
            _backgroundSync?.Wait(cts.Token);
            var report = RunReportAsync(SyncDirection.Push, background: true, cts.Token).GetAwaiter().GetResult();
            _runtime.Log.Info("sync", $"Exit sync: {report.Message}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _runtime.Log.Warn("sync", $"Exit sync failed or timed out: {ex.Message}");
        }
    }

    // ───────────── ISyncService ─────────────

    public Task<SyncReport> PushAsync(CancellationToken cancellationToken = default) => RunReportAsync(SyncDirection.Push, background: false, cancellationToken);

    public Task<SyncReport> PullAsync(CancellationToken cancellationToken = default) => RunReportAsync(SyncDirection.Pull, background: false, cancellationToken);

    /// <summary>Pull and push in one go.</summary>
    public Task<SyncReport> SyncAsync(CancellationToken cancellationToken = default) => RunReportAsync(SyncDirection.Both, background: false, cancellationToken);

    public async Task<SyncReport> StatusAsync(CancellationToken cancellationToken = default)
    {
        if (!Configured)
        {
            return new SyncReport(false, "Sync is off. Set it up with: pk sync init <folder|git-url>", []);
        }

        var state = SyncState.Load(_runtime.Paths.SyncStateFile);
        var last = state.LastSync is { } time ? time.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) : "never";
        try
        {
            var result = await RunAsync(SyncDirection.Both, dryRun: true, background: true, cancellationToken).ConfigureAwait(false);
            var pending = result.Changes.Count;
            var message = $"{Settings.Backend} → {Settings.Target} · last sync {last} · "
                + (pending == 0 ? "up to date" : $"{pending} pending change{(pending == 1 ? string.Empty : "s")}");
            return new SyncReport(true, message, [.. result.Changes, .. result.Conflicts.Select(c => "⚠ " + c)]);
        }
        catch (SyncException ex)
        {
            return new SyncReport(false, $"{Settings.Backend} → {Settings.Target} · last sync {last} · {ex.Message}", []);
        }
    }

    public async Task<SyncReport> InitAsync(string backend, string target, CancellationToken cancellationToken = default)
    {
        target = target.Trim();
        backend = backend is "" or "auto" ? (PluginsGitUrl(target) ? "git" : "folder") : backend.ToLowerInvariant();
        if (backend is not ("folder" or "git"))
        {
            return new SyncReport(false, $"Unknown backend '{backend}'. Use folder or git.", []);
        }

        if (target.Length == 0 || target.StartsWith('-'))
        {
            return new SyncReport(false, $"Invalid sync target '{target}'.", []);
        }

        if (backend == "folder")
        {
            target = Path.GetFullPath(target);
            try
            {
                Directory.CreateDirectory(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new SyncReport(false, $"Can't use {target}: {ex.Message}", []);
            }
        }

        var store = _runtime.ConfigStore;
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Backend and target are machine-specific (folder paths differ per machine), so they live in config.local.json.
            store.SetLocalValue("sync.backend", backend);
            store.SetLocalValue("sync.target", JsonSerializer.Serialize(target));
            File.Delete(_runtime.Paths.SyncStateFile);
            DeleteRepo();
        }
        finally
        {
            _lock.Release();
        }

        var report = await RunReportAsync(SyncDirection.Both, background: false, cancellationToken).ConfigureAwait(false);
        return report with { Message = (report.Success ? $"Sync set up ({backend} → {target}). " : string.Empty) + report.Message };
    }

    public async Task<SyncReport> OffAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var store = _runtime.ConfigStore;
            store.SetLocalValue("sync.backend", "none");
            store.Reset("sync.target", local: true);
            store.Reset("sync.target");
            File.Delete(_runtime.Paths.SyncStateFile);
            DeleteRepo();
            return new SyncReport(true, "Sync is off. Your files are untouched; the sync target was not modified.", []);
        }
        finally
        {
            _lock.Release();
        }
    }

    // ───────────── The sync run ─────────────

    private async Task<SyncReport> RunReportAsync(SyncDirection direction, bool background, CancellationToken cancellationToken)
    {
        if (!Configured)
        {
            return new SyncReport(false, "Sync is off. Set it up with: pk sync init <folder|git-url>", []);
        }

        try
        {
            var result = await RunAsync(direction, dryRun: false, background, cancellationToken).ConfigureAwait(false);
            var summary = result.Changes.Count == 0 && result.Conflicts.Count == 0
                ? $"Already in sync with {Settings.Target}."
                : $"Synced with {Settings.Target}: {result.Changes.Count} change{(result.Changes.Count == 1 ? string.Empty : "s")}"
                    + (result.Conflicts.Count > 0 ? $", {result.Conflicts.Count} conflict{(result.Conflicts.Count == 1 ? string.Empty : "s")}" : string.Empty) + ".";
            if (result.Pending > 0)
            {
                summary += $" {result.Pending} change{(result.Pending == 1 ? string.Empty : "s")} left for {(direction == SyncDirection.Pull ? "pk sync push" : "pk sync pull")}.";
            }

            if (result.AliasesChanged)
            {
                summary += " Alias changes apply after a restart.";
            }

            return new SyncReport(true, summary, [.. result.Changes, .. result.Conflicts.Select(c => "⚠ " + c), .. MissingPlugins()]);
        }
        catch (SyncException ex)
        {
            _runtime.Log.Warn("sync", ex.Message);
            return new SyncReport(false, ex.Message, []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _runtime.Log.Error("sync", "Sync failed", ex);
            return new SyncReport(false, "Sync failed: " + ex.Message, []);
        }
    }

    private async Task<SyncResult> RunAsync(SyncDirection direction, bool dryRun, bool background, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = Settings;
            if (_runtime.ConfigStore.Problems.Any(p => p.Source == JsonConfigStore.BaseSource && p.Path.Length == 0 && p.Severity == ConfigProblemSeverity.Error))
            {
                throw new SyncException("config.json has a syntax error; fix it (pk config edit) before syncing.");
            }

            var stateFile = _runtime.Paths.SyncStateFile;
            var state = SyncState.Load(stateFile);
            if (state.Backend != settings.Backend || state.Target != settings.Target)
            {
                state = new SyncState { Backend = settings.Backend, Target = settings.Target };
            }

            for (var attempt = 1; ; attempt++)
            {
                var remoteRoot = settings.Backend == "git"
                    ? await PrepareGitAsync(settings.Target!, background, cancellationToken).ConfigureAwait(false)
                    : PrepareFolder(settings.Target!);
                var engine = new SyncEngine(
                    _runtime.Paths.ConfigDir,
                    remoteRoot,
                    _runtime.ConfigStore.GetBaseLayer,
                    _runtime.ConfigStore.ReplaceBaseLayer);
                var options = new SyncOptions(direction, settings.SyncHistory, dryRun, PreferRemoteOnConflict: state.LastSync is null);
                var result = engine.Run(state, options);
                if (dryRun)
                {
                    return result;
                }

                if (settings.Backend == "git" && result.RemoteChanged)
                {
                    var pushed = await CommitAndPushAsync(background, cancellationToken).ConfigureAwait(false);
                    if (!pushed.Success)
                    {
                        if (attempt < 2)
                        {
                            _runtime.Log.Info("sync", $"Push rejected ({pushed.Message}); merging again");
                            continue;
                        }

                        throw new SyncException($"git push failed: {pushed.Message}");
                    }
                }

                state.Items = result.NewBase;
                state.LastSync = DateTimeOffset.UtcNow;
                state.LastResult = $"{direction}: {result.Changes.Count} change(s), {result.Conflicts.Count} conflict(s)";
                state.Save(stateFile);
                return result;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private static string PrepareFolder(string target)
    {
        var dir = Path.GetFullPath(target);
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SyncException($"Sync folder {dir} is not available: {ex.Message}");
        }

        return dir;
    }

    private IEnumerable<string> MissingPlugins()
    {
        if (_runtime.Plugins is not Plugins.PluginManager manager)
        {
            yield break;
        }

        foreach (var plugin in Plugins.InstalledPlugins.Read(_runtime.Paths))
        {
            if (manager.Find(plugin.Name) is null && !Directory.Exists(Path.Combine(_runtime.Paths.PluginsDir, plugin.Name)))
            {
                yield return $"plugin {plugin.Name} is used on another machine: pk plugin install {plugin.Location ?? plugin.Name}";
            }
        }
    }

    // ───────────── git backend ─────────────

    private Task<ProcessResult> Git(string? workingDirectory, IEnumerable<string> args, bool background, CancellationToken cancellationToken) =>
        ProcessRunner.RunAsync(
            "git",
            args,
            workingDirectory,
            ProcessRunner.GitEnvironment(allowCredentialUi: !background && _runtime.Shell.IsInteractive),
            TimeSpan.FromMinutes(2),
            cancellationToken);

    /// <summary>Clone or fetch, then make the working copy match the remote branch exactly. Returns the working copy.</summary>
    private async Task<string> PrepareGitAsync(string url, bool background, CancellationToken cancellationToken)
    {
        var repo = RepoDir;
        if (!Directory.Exists(Path.Combine(repo, ".git")))
        {
            DeleteRepo();
            Directory.CreateDirectory(Path.GetDirectoryName(repo)!);
            var clone = await Git(null, ["clone", "--quiet", "--", url, repo], background, cancellationToken).ConfigureAwait(false);
            if (!clone.Success)
            {
                throw new SyncException(clone.ExitCode == ProcessRunner.NotFound ? "git is not installed." : $"git clone failed: {clone.Message}");
            }

            await Git(repo, ["config", "core.autocrlf", "false"], background, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var remote = await Git(repo, ["remote", "get-url", "origin"], background, cancellationToken).ConfigureAwait(false);
            if (remote.StdOut.Trim() != url)
            {
                await Git(repo, ["remote", "set-url", "origin", url], background, cancellationToken).ConfigureAwait(false);
            }

            var fetch = await Git(repo, ["fetch", "--quiet", "--prune", "origin"], background, cancellationToken).ConfigureAwait(false);
            if (!fetch.Success)
            {
                throw new SyncException($"git fetch failed: {fetch.Message}");
            }
        }

        var branch = await RemoteBranchAsync(repo, background, cancellationToken).ConfigureAwait(false);
        if (branch is not null)
        {
            await Git(repo, ["checkout", "--quiet", "-B", branch, "origin/" + branch], background, cancellationToken).ConfigureAwait(false);
            var reset = await Git(repo, ["reset", "--quiet", "--hard", "origin/" + branch], background, cancellationToken).ConfigureAwait(false);
            if (!reset.Success)
            {
                throw new SyncException($"git reset failed: {reset.Message}");
            }
        }

        await Git(repo, ["clean", "-fdq"], background, cancellationToken).ConfigureAwait(false);
        return repo;
    }

    private async Task<string?> RemoteBranchAsync(string repo, bool background, CancellationToken cancellationToken)
    {
        var list = await Git(repo, ["branch", "-r", "--format=%(refname:short)"], background, cancellationToken).ConfigureAwait(false);
        var branches = list.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(b => b.StartsWith("origin/", StringComparison.Ordinal) && b != "origin/HEAD" && b != "origin")
            .Select(b => b["origin/".Length..])
            .ToList();
        if (branches.Count == 0)
        {
            return null;
        }

        var current = (await Git(repo, ["symbolic-ref", "--short", "HEAD"], background, cancellationToken).ConfigureAwait(false)).StdOut.Trim();
        return branches.FirstOrDefault(b => b == current)
            ?? branches.FirstOrDefault(b => b == "main")
            ?? branches.FirstOrDefault(b => b == "master")
            ?? branches[0];
    }

    private async Task<ProcessResult> CommitAndPushAsync(bool background, CancellationToken cancellationToken)
    {
        var repo = RepoDir;
        await Git(repo, ["add", "-A"], background, cancellationToken).ConfigureAwait(false);
        var staged = await Git(repo, ["diff", "--cached", "--quiet"], background, cancellationToken).ConfigureAwait(false);
        if (staged.ExitCode == 1)
        {
            var email = await Git(repo, ["config", "user.email"], background, cancellationToken).ConfigureAwait(false);
            List<string> args = string.IsNullOrWhiteSpace(email.StdOut) ? ["-c", "user.name=Pickle", "-c", "user.email=pickle@localhost"] : [];
            args.AddRange(["commit", "--quiet", "-m", $"Pickle sync from {Environment.MachineName}"]);
            var commit = await Git(repo, args, background, cancellationToken).ConfigureAwait(false);
            if (!commit.Success)
            {
                return commit;
            }
        }

        var branch = (await Git(repo, ["symbolic-ref", "--short", "HEAD"], background, cancellationToken).ConfigureAwait(false)).StdOut.Trim();
        if (branch.Length == 0)
        {
            branch = "main";
        }

        return await Git(repo, ["push", "--quiet", "origin", "HEAD:refs/heads/" + branch], background, cancellationToken).ConfigureAwait(false);
    }

    private void DeleteRepo()
    {
        if (!Directory.Exists(RepoDir))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(RepoDir, "*", SearchOption.AllDirectories))
        {
            // git marks objects read-only, which blocks deletion on Windows.
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(RepoDir, recursive: true);
    }

    private static bool PluginsGitUrl(string target) => Plugins.PluginCommand.IsGitUrl(target) || target.EndsWith(".git", StringComparison.OrdinalIgnoreCase);
}
