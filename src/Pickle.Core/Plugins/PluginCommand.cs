using System.Management.Automation;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pickle.Abstractions;
using Pickle.Core.Commands;

namespace Pickle.Core.Plugins;

/// <summary><c>pk plugin</c>: list, install, remove, enable/disable, trust and scaffold plugins.</summary>
public sealed partial class PluginCommand(PickleRuntime runtime, PluginManager manager) : IPickleCommand
{
    public string Name => "plugin";

    public string Description => "List, install, remove, enable, disable, trust and create plugins";

    public string Usage => """
        pk plugin list                              Plugins and their status
        pk plugin install <name|git-url|path>       From the PowerShell Gallery, a git repository or a folder
        pk plugin remove <id>                       Uninstall
        pk plugin enable <id> | disable <id>        Turn a plugin on or off
        pk plugin trust <id|path>                   Allow a .NET plugin (pins the SHA-256 of its assembly)
        pk plugin new <name> [--dotnet]             Create a plugin project in the current folder
        """;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex ModuleName();

    public async ValueTask<int> ExecuteAsync(PickleCommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        var sub = positional.Count > 0 ? positional[0].ToLowerInvariant() : "list";
        var target = positional.Count > 1 ? positional[1] : null;
        if (sub is not ("list" or "ls" or "new") && target is null)
        {
            context.WriteError($"Usage: pk plugin {sub} <{(sub == "install" ? "name|git-url|path" : "id")}>");
            return 2;
        }

        return sub switch
        {
            "list" or "ls" => List(context),
            "install" or "add" => await InstallAsync(context, target!, cancellationToken).ConfigureAwait(false),
            "remove" or "uninstall" or "rm" => Remove(context, target!),
            "enable" => SetEnabled(context, target!, enabled: true),
            "disable" => SetEnabled(context, target!, enabled: false),
            "trust" => Trust(context, target!),
            "new" => New(context, target, args.Contains("--dotnet")),
            _ => Unknown(context, sub),
        };
    }

    private int List(PickleCommandContext context)
    {
        foreach (var plugin in manager.Plugins.OrderBy(p => p.Kind == PluginInfo.BuiltInKind).ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
        {
            var row = new PSObject();
            row.Properties.Add(new PSNoteProperty("Id", plugin.Id));
            row.Properties.Add(new PSNoteProperty("Kind", plugin.Kind));
            row.Properties.Add(new PSNoteProperty("Status", plugin.Status.ToString()));
            row.Properties.Add(new PSNoteProperty("Detail", plugin.Error ?? plugin.Description ?? plugin.Path ?? string.Empty));
            context.WriteObject(row);
        }

        var missing = InstalledPlugins.Read(runtime.Paths)
            .Where(p => manager.Find(p.Name) is null)
            .ToList();
        foreach (var plugin in missing)
        {
            var row = new PSObject();
            row.Properties.Add(new PSNoteProperty("Id", plugin.Name));
            row.Properties.Add(new PSNoteProperty("Kind", PluginInfo.PowerShellKind));
            row.Properties.Add(new PSNoteProperty("Status", "NotInstalled"));
            row.Properties.Add(new PSNoteProperty("Detail", $"pk plugin install {plugin.Location ?? plugin.Name}"));
            context.WriteObject(row);
        }

        return 0;
    }

    // ───────────── install ─────────────

    private async Task<int> InstallAsync(PickleCommandContext context, string target, CancellationToken cancellationToken)
    {
        var local = Path.GetFullPath(Path.Combine(context.Cwd, target));
        if (Directory.Exists(local) || File.Exists(local))
        {
            return InstallFromPath(context, local);
        }

        if (IsGitUrl(target))
        {
            return await InstallFromGitAsync(context, target, cancellationToken).ConfigureAwait(false);
        }

        if (!ModuleName().IsMatch(target))
        {
            context.WriteError($"'{target}' is not a folder, a git URL or a PowerShell Gallery module name.");
            return 2;
        }

        return InstallFromGallery(context, target);
    }

    private int InstallFromPath(PickleCommandContext context, string source)
    {
        var sourceDir = Directory.Exists(source) ? source : Path.GetDirectoryName(source)!;
        var name = PluginFolderName(sourceDir, File.Exists(source) ? Path.GetFileNameWithoutExtension(source) : null);
        var destination = Path.Combine(runtime.Paths.PluginsDir, name);
        if (PluginManager.IsUnder(sourceDir, runtime.Paths.PluginsDir))
        {
            context.WriteError($"{sourceDir} is already in the plugins folder.");
            return 2;
        }

        if (Directory.Exists(destination))
        {
            context.WriteError($"A plugin named '{name}' is already installed ({destination}). Remove it first: pk plugin remove {name}");
            return 1;
        }

        CopyDirectory(sourceDir, destination);
        InstalledPlugins.Add(runtime.Paths, new InstalledPlugin(name, "path", sourceDir));
        context.WriteHost(Ok() + $"Copied {sourceDir} → {destination}");
        return ActivateInstalled(context, name);
    }

    private async Task<int> InstallFromGitAsync(PickleCommandContext context, string url, CancellationToken cancellationToken)
    {
        var name = RepositoryName(url);
        if (name is null || !ModuleName().IsMatch(name))
        {
            context.WriteError($"Can't derive a plugin name from '{url}'.");
            return 2;
        }

        var destination = Path.Combine(runtime.Paths.PluginsDir, name);
        if (Directory.Exists(destination))
        {
            context.WriteError($"A plugin named '{name}' is already installed ({destination}). Remove it first: pk plugin remove {name}");
            return 1;
        }

        Directory.CreateDirectory(runtime.Paths.PluginsDir);
        context.WriteHost($"Cloning {url} …");
        var result = await ProcessRunner.RunAsync(
            "git",
            ["clone", "--depth", "1", "--", url, destination],
            environment: ProcessRunner.GitEnvironment(allowCredentialUi: context.Interactive),
            timeout: TimeSpan.FromMinutes(5),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            context.WriteError($"git clone failed: {result.Message}");
            return 1;
        }

        InstalledPlugins.Add(runtime.Paths, new InstalledPlugin(name, "git", url));
        context.WriteHost(Ok() + $"Cloned into {destination}");
        return ActivateInstalled(context, name);
    }

    private int InstallFromGallery(PickleCommandContext context, string name)
    {
        context.WriteHost($"Installing {name} from the PowerShell Gallery …");
        var result = MainRunspace.Invoke(
            runtime,
            """
            param($Name)
            $ErrorActionPreference = 'Stop'
            if (Get-Command Install-PSResource -ErrorAction Ignore) {
                try {
                    Install-PSResource -Name $Name -Scope CurrentUser -TrustRepository -Quiet
                    return 'PSResourceGet'
                }
                catch {
                    if (-not (Get-Command Install-Module -ErrorAction Ignore)) { throw }
                }
            }
            Install-Module -Name $Name -Scope CurrentUser -Force -AllowClobber
            'PowerShellGet'
            """,
            new Dictionary<string, object?> { ["Name"] = name });
        if (result.HadErrors)
        {
            context.WriteError($"Installing {name} failed: {result.Errors[^1]}");
            return 1;
        }

        InstalledPlugins.Add(runtime.Paths, new InstalledPlugin(name, "gallery", name));
        context.WriteHost(Ok() + $"Installed {name} ({result.Output.LastOrDefault()})");
        var info = manager.LoadPowerShell(new PowerShellPluginCandidate(name, name, null, "installed"));
        return Report(context, info);
    }

    /// <summary>Load a plugin that was just copied/cloned into the plugins folder.</summary>
    private int ActivateInstalled(PickleCommandContext context, string folderName)
    {
        var dir = Path.Combine(runtime.Paths.PluginsDir, folderName);
        var dotnet = manager.DiscoverDotnet().Where(c => string.Equals(c.Directory, dir, StringComparison.OrdinalIgnoreCase)).ToList();
        if (dotnet.Count > 0)
        {
            foreach (var candidate in dotnet)
            {
                var hash = AssemblyInspector.Sha256(candidate.AssemblyPath);
                if (manager.IsTrusted(hash))
                {
                    foreach (var info in manager.LoadDotnet(candidate))
                    {
                        Report(context, info);
                    }
                }
                else
                {
                    context.WriteHost(Warn() + $".NET plugins run with your permissions, so they load only after you trust this exact build (sha256 {hash}):");
                    context.WriteHost($"    pk plugin trust {candidate.Id}");
                }
            }

            return 0;
        }

        if (PluginManager.FindModuleFile(dir, folderName, manifestOnly: false) is not { } module)
        {
            context.WriteError($"No PowerShell module (.psd1/.psm1) or .NET plugin (plugin.json / *.dll) found in {dir}.");
            return 1;
        }

        return Report(context, manager.LoadPowerShell(new PowerShellPluginCandidate(Path.GetFileNameWithoutExtension(module), module, module, "plugins-dir")));
    }

    private static string PluginFolderName(string sourceDir, string? fileName)
    {
        var manifest = Path.Combine(sourceDir, "plugin.json");
        if (File.Exists(manifest))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(manifest))?["id"]?.GetValue<string>() is { Length: > 0 } id && PluginScaffold.ValidName().IsMatch(id))
                {
                    return id;
                }
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
            {
            }
        }

        var dirName = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourceDir));
        if (fileName is not null && PluginManager.FindModuleFile(sourceDir, dirName, manifestOnly: false) is null)
        {
            return fileName;
        }

        return PluginManager.FindModuleFile(sourceDir, dirName, manifestOnly: false) is { } module
            ? Path.GetFileNameWithoutExtension(module)
            : dirName;
    }

    internal static bool IsGitUrl(string text) =>
        text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("git://", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
        || GitScpUrl().IsMatch(text);

    [GeneratedRegex(@"^[A-Za-z0-9._-]+@[A-Za-z0-9.-]+:[^\s]+$")]
    private static partial Regex GitScpUrl();

    internal static string? RepositoryName(string url)
    {
        var trimmed = url.TrimEnd('/');
        var slash = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf(':'));
        var last = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        if (last.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            last = last[..^4];
        }

        return last.Length == 0 ? null : last;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(dir);
            if (name is ".git" or "obj")
            {
                continue;
            }

            CopyDirectory(dir, Path.Combine(destination, name));
        }
    }

    // ───────────── remove / enable / disable / trust ─────────────

    private int Remove(PickleCommandContext context, string id)
    {
        var info = manager.Find(id);
        if (info is { Kind: PluginInfo.BuiltInKind })
        {
            context.WriteError($"'{id}' is built into Pickle and can't be removed.");
            return 1;
        }

        var installed = InstalledPlugins.Read(runtime.Paths).FirstOrDefault(p => string.Equals(p.Name, id, StringComparison.OrdinalIgnoreCase));
        var folder = FindPluginFolder(id, info);
        if (info is null && installed is null && folder is null)
        {
            return NotFound(context, id);
        }

        if (info is { Kind: PluginInfo.PowerShellKind })
        {
            MainRunspace.Invoke(runtime, "param($Name) Remove-Module -Name $Name -Force -ErrorAction Ignore", new Dictionary<string, object?> { ["Name"] = info.Id });
        }

        if (installed is { Source: "gallery" })
        {
            var result = MainRunspace.Invoke(
                runtime,
                """
                param($Name)
                if (Get-Command Uninstall-PSResource -ErrorAction Ignore) { Uninstall-PSResource -Name $Name -Scope CurrentUser -ErrorAction Stop }
                else { Uninstall-Module -Name $Name -AllVersions -Force -ErrorAction Stop }
                """,
                new Dictionary<string, object?> { ["Name"] = installed.Name });
            if (result.HadErrors)
            {
                context.WriteHost(Warn() + $"Uninstalling the module failed: {result.Errors[0]}");
            }
        }

        if (folder is not null)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                context.WriteError($"Could not delete {folder} ({ex.Message}). Restart Pickle and run this again.");
                return 1;
            }
        }

        InstalledPlugins.Remove(runtime.Paths, id);
        runtime.ConfigStore.Update(c =>
        {
            c.Plugins.Disabled.RemoveAll(d => string.Equals(d, id, StringComparison.OrdinalIgnoreCase));
            if (info?.Hash is { } hash)
            {
                c.Plugins.TrustedAssemblies.RemoveAll(t => AssemblyInspector.NormalizeHash(t) == hash);
            }
        });
        if (info is not null)
        {
            manager.Forget(info);
        }

        context.WriteHost(Ok() + $"Removed {id}. Its commands and segments disappear after a restart.");
        return 0;
    }

    private int SetEnabled(PickleCommandContext context, string id, bool enabled)
    {
        var info = manager.Find(id);
        if (info is { Kind: PluginInfo.BuiltInKind })
        {
            context.WriteError($"'{id}' is built into Pickle and is always on.");
            return 1;
        }

        if (info is null && FindPluginFolder(id, null) is null && !InstalledPlugins.Read(runtime.Paths).Any(p => string.Equals(p.Name, id, StringComparison.OrdinalIgnoreCase)))
        {
            return NotFound(context, id);
        }

        runtime.ConfigStore.Update(c =>
        {
            c.Plugins.Disabled.RemoveAll(d => string.Equals(d, id, StringComparison.OrdinalIgnoreCase));
            if (!enabled)
            {
                c.Plugins.Disabled.Add(info?.Id ?? id);
            }
        });

        if (!enabled)
        {
            if (info is { Kind: PluginInfo.PowerShellKind, Status: PluginStatus.Loaded })
            {
                MainRunspace.Invoke(runtime, "param($Name) Remove-Module -Name $Name -Force -ErrorAction Ignore", new Dictionary<string, object?> { ["Name"] = info.Id });
            }

            if (info is not null)
            {
                info.Status = PluginStatus.Disabled;
            }

            context.WriteHost(Ok() + $"Disabled {id}. Restart Pickle to remove everything it registered.");
            return 0;
        }

        if (info is { Status: PluginStatus.Loaded })
        {
            context.WriteHost(Ok() + $"{id} is enabled.");
            return 0;
        }

        var loaded = manager.LoadThirdParty().Where(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)).ToList();
        context.WriteHost(Ok() + $"Enabled {id}.");
        foreach (var notice in manager.Notices)
        {
            context.WriteHost(Warn() + notice);
        }

        manager.Notices.Clear();
        return loaded.Any(p => p.Status == PluginStatus.Failed) ? 1 : 0;
    }

    private int Trust(PickleCommandContext context, string target)
    {
        var candidates = ResolveTrustTarget(context, target);
        if (candidates.Count == 0)
        {
            context.WriteError($"No .NET plugin named '{target}' in {runtime.Paths.PluginsDir} (and no such assembly file).");
            return 1;
        }

        foreach (var (id, assembly, candidate) in candidates)
        {
            var hash = AssemblyInspector.Sha256(assembly);
            if (!manager.IsTrusted(hash))
            {
                runtime.ConfigStore.Update(c => c.Plugins.TrustedAssemblies.Add(hash));
            }

            context.WriteHost(Ok() + $"Trusted {id} (sha256 {hash}). Rebuilding or updating it requires trusting it again.");
            if (candidate is not null && manager.ThirdPartyEnabled && manager.Find(candidate.Id, PluginInfo.DotnetKind) is not { Status: PluginStatus.Loaded })
            {
                foreach (var info in manager.LoadDotnet(candidate))
                {
                    Report(context, info);
                }
            }
        }

        return 0;
    }

    private List<(string Id, string Assembly, DotnetPluginCandidate? Candidate)> ResolveTrustTarget(PickleCommandContext context, string target)
    {
        var discovered = manager.DiscoverDotnet();
        var byId = discovered.Where(c => string.Equals(c.Id, target, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileName(c.Directory), target, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byId.Count == 0 && manager.Find(target, PluginInfo.DotnetKind) is { Path: { } loadedPath })
        {
            byId = [.. discovered.Where(c => string.Equals(c.AssemblyPath, loadedPath, StringComparison.OrdinalIgnoreCase))];
        }

        if (byId.Count > 0)
        {
            return [.. byId.Select(c => (c.Id, c.AssemblyPath, (DotnetPluginCandidate?)c))];
        }

        var path = Path.GetFullPath(Path.Combine(context.Cwd, target));
        if (File.Exists(path) && path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            var match = discovered.FirstOrDefault(c => string.Equals(c.AssemblyPath, path, StringComparison.OrdinalIgnoreCase));
            return [(match?.Id ?? Path.GetFileNameWithoutExtension(path), path, match)];
        }

        if (Directory.Exists(path))
        {
            return [.. discovered.Where(c => string.Equals(c.Directory, path, StringComparison.OrdinalIgnoreCase)).Select(c => (c.Id, c.AssemblyPath, (DotnetPluginCandidate?)c))];
        }

        return [];
    }

    private string? FindPluginFolder(string id, PluginInfo? info)
    {
        var direct = Path.Combine(runtime.Paths.PluginsDir, id);
        if (PluginScaffold.ValidName().IsMatch(id) && Directory.Exists(direct))
        {
            return direct;
        }

        if (info?.Path is { } path && PluginManager.IsUnder(path, runtime.Paths.PluginsDir))
        {
            var relative = Path.GetRelativePath(runtime.Paths.PluginsDir, path);
            var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            return Path.Combine(runtime.Paths.PluginsDir, first);
        }

        return null;
    }

    // ───────────── new ─────────────

    private int New(PickleCommandContext context, string? name, bool dotnet)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            context.WriteError("Usage: pk plugin new <name> [--dotnet]");
            return 2;
        }

        var files = PluginScaffold.Create(context.Cwd, name, dotnet);
        context.WriteHost(Ok() + $"Created {(dotnet ? ".NET" : "PowerShell")} plugin '{name}':");
        foreach (var file in files)
        {
            context.WriteHost("    " + Path.GetRelativePath(context.Cwd, file));
        }

        context.WriteHost(dotnet
            ? $"Build it (dotnet publish -c Release -o out), then: pk plugin install ./{name}/out ; pk plugin trust {name.ToLowerInvariant()}"
            : $"Try it: pk plugin install ./{name}");
        return 0;
    }

    // ───────────── helpers ─────────────

    private int Report(PickleCommandContext context, PluginInfo info)
    {
        switch (info.Status)
        {
            case PluginStatus.Loaded:
                context.WriteHost(Ok() + $"Loaded {info.Id}" + (info.Contributions.Count > 0 ? $" ({string.Join(", ", info.Contributions)})" : string.Empty));
                return 0;
            case PluginStatus.Disabled:
                context.WriteHost(Warn() + $"{info.Id} is disabled (pk plugin enable {info.Id}).");
                return 0;
            default:
                context.WriteError($"{info.Id}: {info.Error}");
                return 1;
        }
    }

    private int NotFound(PickleCommandContext context, string id)
    {
        var suggestions = DidYouMean.Suggest(id, manager.Plugins.Select(p => p.Id));
        context.WriteError($"No plugin '{id}'." + (suggestions.Count > 0 ? $" Did you mean {string.Join(" or ", suggestions.Select(s => $"'{s}'"))}?" : " See: pk plugin list"));
        return 1;
    }

    private static int Unknown(PickleCommandContext context, string sub)
    {
        string[] subcommands = ["list", "install", "remove", "enable", "disable", "trust", "new"];
        var hint = DidYouMean.Suggest(sub, subcommands) is [var best, ..] ? $" Did you mean 'pk plugin {best}'?" : " Run 'pk plugin --help'.";
        context.WriteError($"Unknown subcommand 'pk plugin {sub}'.{hint}");
        return 2;
    }

    private string Ok() => Ansi.Colorize("✔ ", runtime.Themes.Current.Ui.Success);

    private string Warn() => Ansi.Colorize("⚠ ", runtime.Themes.Current.Ui.Warning);
}
