using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Windows.Sandbox;

namespace Pickle.Windows.Commands;

/// <summary><c>pk sandbox</c>: Windows Sandbox from the command line (no arguments opens the panel).</summary>
internal sealed class SandboxCommand : WindowsCommandBase
{
    public override string Name => "sandbox";

    public override string Description => "Windows Sandbox: presets, saved setups, launch with shared folders, winget packages and Pickle inside";

    public override string Usage =>
        "pk sandbox                      (open the panel, Alt+X)\n" +
        "pk sandbox run [preset|saved] [--network|--no-network] [--map <dir>] [--map-rw <dir>] [--winget <id,id>]\n" +
        "               [--pickle] [--url <url>] [--memory <MB>] [--dark] [--protected]\n" +
        "pk sandbox list | status | enable\n" +
        "pk sandbox export <preset|saved> <file.wsb>\n" +
        "pk sandbox rm <saved>";

    protected override async Task<int> RunAsync(CommandOutput output, IReadOnlyList<string> rawArgs, CancellationToken cancellationToken)
    {
        var args = CommandArgs.Parse(rawArgs, "map", "map-rw", "winget", "url", "memory");
        if (args.Error is not null)
        {
            return UsageError(output, args.Error);
        }

        var sandbox = output.Pickle.Services.Get<ISandboxService>();
        if (args.Arg(0) is null)
        {
            if (output.Pickle.Services.Get<IPanelHost>() is { } host && output.Pickle.Panels.Get("sandbox") is not null)
            {
                host.Show("sandbox");
                return 0;
            }

            return UsageError(output);
        }

        if (sandbox is not { IsSupported: true })
        {
            output.Context.WriteError("Windows Sandbox is only available on Windows.");
            return 1;
        }

        switch (args.Arg(0)!.ToLowerInvariant())
        {
            case "list" or "ls":
                foreach (var preset in sandbox.Presets)
                {
                    output.Object(new SandboxRow(preset.Name, "preset", preset.Description));
                }

                foreach (var saved in sandbox.LoadSaved())
                {
                    output.Object(new SandboxRow(saved.Name, "saved", saved.Description));
                }

                return 0;

            case "status":
                var status = sandbox.GetStatus();
                if (!status.Supported)
                {
                    output.Failure(status.Message ?? "Windows Sandbox isn't supported here.");
                }
                else if (!status.FeatureEnabled)
                {
                    output.Warning(status.Message ?? "Windows Sandbox is turned off.");
                }
                else
                {
                    output.Success(status.Running ? "Windows Sandbox is on and a sandbox is running." : "Windows Sandbox is on.");
                }

                output.Object(status);
                return status.FeatureEnabled ? 0 : 1;

            case "enable":
                if (!output.Context.Confirm("Turn on Windows Sandbox? This needs administrator rights and usually a restart.", true))
                {
                    return 1;
                }

                var enabled = await sandbox.EnableFeatureAsync(new Progress<string>(output.Status), cancellationToken).ConfigureAwait(false);
                (enabled.Success ? (Action<string>)output.Success : output.Failure)(enabled.Message);
                return enabled.Success ? 0 : 1;

            case "rm" or "delete":
                if (args.Arg(1) is not { } name)
                {
                    return UsageError(output, "Name the saved sandbox to delete.");
                }

                if (!sandbox.Delete(name))
                {
                    output.Context.WriteError($"No saved sandbox named '{name}'.");
                    return 1;
                }

                output.Success($"Deleted '{name}'.");
                return 0;

            case "export":
                if (args.Arg(1) is not { } source || args.Arg(2) is not { } file)
                {
                    return UsageError(output, "pk sandbox export <preset|saved> <file.wsb>");
                }

                if (Find(sandbox, source) is not { } toExport)
                {
                    output.Context.WriteError($"No preset or saved sandbox named '{source}'. See pk sandbox list.");
                    return 1;
                }

                var target = Path.IsPathFullyQualified(file) ? file : Path.Combine(output.Context.Cwd, file);
                var exported = sandbox.Export(toExport, target);
                (exported.Success ? (Action<string>)output.Success : output.Failure)(exported.Message);
                return exported.Success ? 0 : 1;

            case "run" or "start":
                var config = args.Arg(1) is { } from ? Find(sandbox, from) : new SandboxConfig { Name = "Quick" };
                if (config is null)
                {
                    output.Context.WriteError($"No preset or saved sandbox named '{args.Arg(1)}'. See pk sandbox list.");
                    return 1;
                }

                Apply(config, args, output.Context.Cwd);
                var launched = await sandbox.LaunchAsync(config, cancellationToken).ConfigureAwait(false);
                (launched.Success ? (Action<string>)output.Success : output.Failure)(launched.Message);
                return launched.Success ? 0 : 1;

            case var sub:
                return UsageError(output, $"Unknown subcommand 'pk sandbox {sub}'.");
        }
    }

    internal static SandboxConfig? Find(ISandboxService sandbox, string name) =>
        sandbox.LoadSaved().FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? sandbox.Presets.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? sandbox.Presets.FirstOrDefault(c => c.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase));

    private static void Apply(SandboxConfig config, CommandArgs args, string cwd)
    {
        if (args.Has("network"))
        {
            config.Networking = SandboxSwitch.Enable;
        }

        if (args.Has("no-network"))
        {
            config.Networking = SandboxSwitch.Disable;
        }

        if (args.Value("map") is { } map)
        {
            config.MappedFolders.Add(new SandboxMappedFolder { HostFolder = Full(map, cwd), ReadOnly = true });
        }

        if (args.Value("map-rw") is { } mapRw)
        {
            config.MappedFolders.Add(new SandboxMappedFolder { HostFolder = Full(mapRw, cwd), ReadOnly = false });
        }

        if (args.Value("winget") is { } ids)
        {
            config.WingetPackages.AddRange(ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            config.Networking = config.Networking == SandboxSwitch.Disable ? SandboxSwitch.Disable : SandboxSwitch.Enable;
        }

        if (args.Has("pickle"))
        {
            config.IncludePickle = true;
            config.StartPickle = true;
        }

        if (args.Value("url") is { } url)
        {
            config.StartUrl = url;
        }

        if (args.Value("memory") is { } memory)
        {
            config.MemoryInMB = int.TryParse(memory, out var mb) ? mb : throw new ArgumentException($"--memory must be a number of MB, not '{memory}'.");
        }

        if (args.Has("dark"))
        {
            config.DarkMode = true;
        }

        if (args.Has("protected"))
        {
            config.ProtectedClient = SandboxSwitch.Enable;
        }
    }

    private static string Full(string path, string cwd) => Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(cwd, path));

    internal sealed record SandboxRow(string Name, string Kind, string Description);
}
