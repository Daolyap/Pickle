using System.Runtime.InteropServices;
using Pickle.Abstractions;
using Pickle.Core.Config;
using Pickle.Core.Plugins;

namespace Pickle.Core.Commands;

/// <summary>Small `pk` commands owned by Core itself. Workstreams add their own IPickleCommand classes next to their features.</summary>
public sealed class VersionCommand : IPickleCommand
{
    public string Name => "version";

    public string Description => "Show Pickle, PowerShell and .NET versions";

    public string Usage => "pk version";

    public ValueTask<int> ExecuteAsync(PickleCommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        context.WriteObject(new
        {
            Pickle = PickleRuntime.Version,
            PowerShell = PickleRuntime.PowerShellVersion,
            DotNet = Environment.Version.ToString(),
            OS = RuntimeInformation.OSDescription,
            ConfigDir = context.Pickle.Paths.ConfigDir,
        });
        return ValueTask.FromResult(0);
    }
}

public sealed record DoctorCheck(string Status, string Check, string Detail)
{
    public const string Ok = "✔";
    public const string Warning = "⚠";
    public const string Error = "✖";
    public const string Info = "·";
}

/// <summary><c>pk doctor</c>: environment, dependencies, config and plugin health.</summary>
public sealed class DoctorCommand(PickleRuntime runtime) : IPickleCommand
{
    public string Name => "doctor";

    public string Description => "Check versions, paths, tools, config and plugins for problems";

    public string Usage => "pk doctor";

    public async ValueTask<int> ExecuteAsync(PickleCommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var checks = await RunChecksAsync(cancellationToken).ConfigureAwait(false);
        foreach (var check in checks)
        {
            context.WriteObject(check);
        }

        var errors = checks.Count(c => c.Status == DoctorCheck.Error);
        var warnings = checks.Count(c => c.Status == DoctorCheck.Warning);
        var theme = runtime.Themes.Current;
        context.WriteHost(errors > 0
            ? Ansi.Colorize($"{errors} problem(s), {warnings} warning(s)", theme.Ui.Error)
            : Ansi.Colorize(warnings > 0 ? $"No problems, {warnings} warning(s)" : "Everything looks good", warnings > 0 ? theme.Ui.Warning : theme.Ui.Success));
        return errors > 0 ? 1 : 0;
    }

    public async Task<IReadOnlyList<DoctorCheck>> RunChecksAsync(CancellationToken cancellationToken)
    {
        var checks = new List<DoctorCheck>();
        void Add(string status, string check, string detail) => checks.Add(new DoctorCheck(status, check, detail));

        Add(DoctorCheck.Ok, "Pickle", $"{PickleRuntime.Version} · PowerShell {PickleRuntime.PowerShellVersion} · .NET {Environment.Version}");
        Add(DoctorCheck.Info, "OS", $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");

        var paths = runtime.Paths;
        foreach (var (label, dir) in new[] { ("Config dir", paths.ConfigDir), ("Data dir", paths.DataDir) })
        {
            Add(IsWritable(dir) ? DoctorCheck.Ok : DoctorCheck.Error, label, IsWritable(dir) ? dir : $"{dir} is missing or not writable");
        }

        var modulePath = (Environment.GetEnvironmentVariable("PSModulePath") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var missing = modulePath.Where(p => !Directory.Exists(p)).ToList();
        // PowerShell lists its default locations even when they don't exist; that's informational, not a problem.
        Add(
            missing.Count == 0 ? DoctorCheck.Ok : DoctorCheck.Info,
            "PSModulePath",
            $"{modulePath.Length} entries" + (missing.Count > 0 ? $"; not present: {string.Join(", ", missing)}" : string.Empty));

        var pwsh = EditorLauncher.FindOnPath(OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh");
        Add(pwsh is null ? DoctorCheck.Info : DoctorCheck.Ok, "pwsh", pwsh ?? "not installed (optional; its bundled modules are used when present)");

        var git = await ProcessRunner.RunAsync("git", ["--version"], timeout: TimeSpan.FromSeconds(5), cancellationToken: cancellationToken).ConfigureAwait(false);
        Add(git.Success ? DoctorCheck.Ok : DoctorCheck.Warning, "git", git.Success ? git.StdOut.Trim() : "not found: the git panel, git prompt segment and git sync need it");

        if (OperatingSystem.IsWindows())
        {
            var winget = EditorLauncher.FindOnPath("winget.exe");
            Add(winget is null ? DoctorCheck.Warning : DoctorCheck.Ok, "winget", winget ?? "not found: install App Installer from the Microsoft Store");
        }
        else
        {
            Add(DoctorCheck.Info, "winget", "n/a (Windows only)");
        }

        var terminal = runtime.Terminal;
        Add(
            terminal.SupportsAnsi ? DoctorCheck.Ok : DoctorCheck.Warning,
            "Terminal",
            (terminal.SupportsAnsi ? "VT/ANSI supported" : "VT/ANSI not available (output is redirected or the console is too old)") + $" · {terminal.Width}x{terminal.Height}");

        var problems = runtime.ConfigStore.Problems;
        if (problems.Count == 0)
        {
            Add(DoctorCheck.Ok, "Config", runtime.ConfigStore.ConfigFile);
        }

        foreach (var problem in problems)
        {
            Add(problem.Severity == ConfigProblemSeverity.Error ? DoctorCheck.Error : DoctorCheck.Warning, "Config", problem.ToString());
        }

        Add(runtime.Themes.Current.Name.Equals(runtime.Config.Current.Theme, StringComparison.OrdinalIgnoreCase) ? DoctorCheck.Ok : DoctorCheck.Warning, "Theme",
            runtime.Themes.Current.Name.Equals(runtime.Config.Current.Theme, StringComparison.OrdinalIgnoreCase)
                ? runtime.Themes.Current.Name
                : $"'{runtime.Config.Current.Theme}' not found; using '{runtime.Themes.Current.Name}'");

        if (runtime.Plugins is PluginManager plugins)
        {
            var all = plugins.Plugins;
            Add(DoctorCheck.Ok, "Plugins", $"{all.Count(p => p.Status == PluginStatus.Loaded)} loaded" + (plugins.ThirdPartyEnabled ? string.Empty : " (third-party plugins off)"));
            foreach (var plugin in all.Where(p => p.Status is PluginStatus.Failed or PluginStatus.Untrusted))
            {
                Add(plugin.Status == PluginStatus.Failed ? DoctorCheck.Error : DoctorCheck.Warning, $"Plugin {plugin.Id}", plugin.Error ?? plugin.Status.ToString());
            }
        }

        var sync = runtime.Config.Current.Sync;
        if (sync.Backend == "none")
        {
            Add(DoctorCheck.Info, "Sync", "off (pk sync init <folder|git-url>)");
        }
        else
        {
            var status = await runtime.Sync.StatusAsync(cancellationToken).ConfigureAwait(false);
            Add(status.Success ? DoctorCheck.Ok : DoctorCheck.Warning, "Sync", status.Message);
        }

        Add(DoctorCheck.Info, "Profile", File.Exists(paths.ProfileFile) ? paths.ProfileFile : "none (Edit-PickleProfile to create one)");
        return checks;
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".doctor-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary><c>pk reload</c>: re-read config and re-apply what can be applied without restarting.</summary>
public sealed class ReloadCommand(PickleRuntime runtime) : IPickleCommand
{
    public string Name => "reload";

    public string Description => "Reload config, theme, aliases and new plugins without restarting";

    public string Usage => "pk reload";

    public ValueTask<int> ExecuteAsync(PickleCommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var theme = runtime.Themes.Current;
        var ok = Ansi.Colorize("✔ ", theme.Ui.Success);
        var warn = Ansi.Colorize("⚠ ", theme.Ui.Warning);
        var failures = 0;

        runtime.ConfigStore.Reload();
        var problems = runtime.ConfigStore.Problems;
        context.WriteHost((problems.Count == 0 ? ok : warn) + "config" + (problems.Count == 0 ? string.Empty : $" ({problems.Count} problem(s): pk config)"));

        try
        {
            runtime.Themes.Apply(runtime.Config.Current.Theme);
            context.WriteHost(ok + "theme " + runtime.Themes.Current.Name);
        }
        catch (ArgumentException ex)
        {
            failures++;
            context.WriteHost(warn + ex.Message);
        }

        failures += Run(context, "aliases", runtime.AliasMaterializer.BuildAliasScript(context.Cwd), ok, warn);
        failures += Run(context, "translation shims", runtime.Translation.BuildShimScript(), ok, warn);

        if (runtime.Plugins is PluginManager plugins && plugins.ThirdPartyEnabled)
        {
            var loaded = plugins.LoadThirdParty();
            var names = loaded.Where(p => p.Status == PluginStatus.Loaded).Select(p => p.Id).ToList();
            context.WriteHost(ok + "plugins" + (names.Count > 0 ? $": loaded {string.Join(", ", names)}" : string.Empty));
            foreach (var notice in plugins.Notices)
            {
                context.WriteHost(warn + notice);
            }

            plugins.Notices.Clear();
            foreach (var failed in loaded.Where(p => p.Status == PluginStatus.Failed))
            {
                failures++;
                context.WriteHost(warn + $"{failed.Id}: {failed.Error}");
            }
        }

        context.WriteHost(Ansi.Colorize("Removed or updated plugins and .NET assemblies take effect after a restart.", theme.Ui.Muted));
        return ValueTask.FromResult(failures == 0 ? 0 : 1);
    }

    private int Run(PickleCommandContext context, string what, string script, string ok, string warn)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return 0;
        }

        var result = MainRunspace.Invoke(runtime, script);
        if (result.HadErrors)
        {
            context.WriteHost(warn + what + ": " + result.Errors[0]);
            return 1;
        }

        context.WriteHost(ok + what);
        return 0;
    }
}

/// <summary><c>pk paths</c>: where Pickle keeps its files.</summary>
public sealed class PathsCommand(PickleRuntime runtime) : IPickleCommand
{
    public string Name => "paths";

    public string Description => "Show where Pickle keeps config, data, plugins and logs";

    public string Usage => "pk paths";

    public ValueTask<int> ExecuteAsync(PickleCommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var p = runtime.Paths;
        var entries = new (string Name, string Path)[]
        {
            ("Config", p.ConfigDir),
            ("Settings", p.ConfigFile),
            ("LocalSettings", p.LocalConfigFile),
            ("Aliases", p.AliasesFile),
            ("History", p.HistoryFile),
            ("Profile", p.ProfileFile),
            ("Themes", p.ThemesDir),
            ("Wizards", p.WizardsDir),
            ("Plugins", p.PluginsDir),
            ("Data", p.DataDir),
            ("Modules", Path.Combine(p.DataDir, "modules")),
            ("SyncState", p.SyncStateFile),
            ("Logs", p.LogDir),
            ("Cache", p.CacheDir),
        };
        foreach (var (name, path) in entries)
        {
            context.WriteObject(new PathEntry(name, path, File.Exists(path) || Directory.Exists(path)));
        }

        return ValueTask.FromResult(0);
    }
}

public sealed record PathEntry(string Name, string Path, bool Exists);
