using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Tui;

namespace Pickle.Modules.Distros;

/// <summary><c>pk wsl</c> / <c>pk distros</c>: the panel interactively; <c>list</c> and one-line actions for scripts.</summary>
internal sealed class DistroCommand(string name) : PanelCommand
{
    private static readonly string[] LifecycleVerbs = ["start", "stop", "restart", "remove", "default", "upgrade"];

    public override string Name => name;

    public override string Description => "WSL distributions and Distrobox, Toolbx, LXC and Incus containers";

    public override string Usage =>
        $"pk {name} | list [filter] | start|stop|restart|remove|default|upgrade|enter <name> [--backend id] [--yes] | new <name> [--image ref] [--backend id] | export <name> [file] | import <name> <dir> <file> [--version 1|2] | set-version <name> <1|2> | online | update | shutdown";

    public override IReadOnlyList<string> Examples =>
    [
        $"pk {name}",
        $"pk {name} list",
        $"pk {name} stop Ubuntu",
        $"pk {name} enter dev",
        $"pk {name} new dev --image ubuntu:24.04 --backend distrobox",
        $"pk {name} export Ubuntu D:\\backups\\ubuntu.tar",
        $"pk {name} online",
    ];

    protected override string PanelId => DistroPanel.PanelId;

    protected override string? PanelArgument(IReadOnlyList<string> args) => null;

    protected override async Task<int> RunAsync(CommandOutput output, IReadOnlyList<string> raw, CancellationToken cancellationToken)
    {
        var verb = raw.Count > 0 ? raw[0].ToLowerInvariant() : string.Empty;
        var rest = raw.Skip(1).ToList();
        var service = output.Pickle.Services.Require<DistroService>();
        switch (verb)
        {
            case "rm":
                return await LifecycleAsync(output, service, "remove", rest, cancellationToken).ConfigureAwait(false);
            case "enter":
                return await EnterAsync(output, service, rest, cancellationToken).ConfigureAwait(false);
            case "new" or "create" or "install":
                return NewAsync(output, service, rest);
            case "export":
                return await ExportAsync(output, service, rest, cancellationToken).ConfigureAwait(false);
            case "import":
                return await ImportAsync(output, service, rest, cancellationToken).ConfigureAwait(false);
            case "set-version":
                return await SetVersionAsync(output, service, rest, cancellationToken).ConfigureAwait(false);
            case "online":
                return await OnlineAsync(output, service, cancellationToken).ConfigureAwait(false);
            case "update":
                return await StreamAsync(output, service, WslBackend.Update, "Updated WSL.", cancellationToken).ConfigureAwait(false);
            case "shutdown":
                return await ShutdownAsync(output, service, rest, cancellationToken).ConfigureAwait(false);
            default:
                return LifecycleVerbs.Contains(verb)
                    ? await LifecycleAsync(output, service, verb, rest, cancellationToken).ConfigureAwait(false)
                    : await base.RunAsync(output, raw, cancellationToken).ConfigureAwait(false);
        }
    }

    protected override async Task<int> ListAsync(CommandOutput output, IReadOnlyList<string> raw, CancellationToken cancellationToken)
    {
        var args = CommandArgs.Parse(raw, "backend");
        var service = output.Pickle.Services.Require<DistroService>();
        if (service.Available.Count == 0)
        {
            output.Failure("None of wsl.exe, distrobox, toolbox, lxc or incus was found on PATH.");
            return 1;
        }

        var listing = await service.ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var error in listing.Errors)
        {
            output.Warning(error);
        }

        foreach (var distro in listing.Items.Where(d => (args.Arg(0) is null || d.Name.Contains(args.Arg(0)!, StringComparison.OrdinalIgnoreCase)) && (args.Value("backend") is null || d.Backend == args.Value("backend"))))
        {
            output.Object(Display.Columns(distro, "Name", "State", "Backend", "Summary", "IsDefault"));
        }

        return listing.Errors.Count > 0 && listing.Items.Count == 0 ? 1 : 0;
    }

    private async Task<int> LifecycleAsync(CommandOutput output, DistroService service, string verb, List<string> rest, CancellationToken cancellationToken)
    {
        var args = CommandArgs.Parse(rest, "backend");
        if (args.Arg(0) is not { } target)
        {
            return UsageError(output, $"Name what to {verb}.");
        }

        var distro = await FindAsync(output, service, target, args, cancellationToken).ConfigureAwait(false);
        if (distro is null)
        {
            return 1;
        }

        if (!service.Supports(distro, verb))
        {
            output.Failure($"{service.BackendOf(distro).DisplayName} has no '{verb}' for {distro.Name}.");
            return 1;
        }

        var question = verb == "remove"
            ? $"Remove {distro.Name}? This deletes it and everything stored inside it."
            : verb == "stop" && distro.IsRunning ? $"Stop {distro.Name}?" : null;
        if (question is not null && !output.Confirm(args, question, defaultYes: false))
        {
            output.Muted("Cancelled.");
            return 1;
        }

        var result = await service.ActAsync(distro, verb, cancellationToken).ConfigureAwait(false);
        return Report(output, result);
    }

    private async Task<int> EnterAsync(CommandOutput output, DistroService service, List<string> rest, CancellationToken cancellationToken)
    {
        var args = CommandArgs.Parse(rest, "backend");
        if (args.Arg(0) is not { } target)
        {
            return UsageError(output, "Name what to enter.");
        }

        var distro = await FindAsync(output, service, target, args, cancellationToken).ConfigureAwait(false);
        if (distro is null)
        {
            return 1;
        }

        var line = service.BackendOf(distro).ShellLine(distro, "enter");
        if (output.Pickle.Shell.IsInteractive && line is not null)
        {
            output.Pickle.Shell.SubmitCommand(line);
            return 0;
        }

        output.Failure("Entering needs an interactive session. Run: " + line);
        return 1;
    }

    private int NewAsync(CommandOutput output, DistroService service, List<string> rest)
    {
        var args = CommandArgs.Parse(rest, "backend", "image");
        if (args.Arg(0) is not { } name)
        {
            return UsageError(output, "Name the new one.");
        }

        var available = service.Available;
        var backend = args.Value("backend") is { } id
            ? available.FirstOrDefault(b => b.Id == id) ?? throw new ArgumentException($"'{id}' is not available here. Available: {string.Join(", ", available.Select(b => b.Id))}.")
            : available.Count == 1
                ? available[0]
                : throw new ArgumentException(available.Count == 0 ? "None of wsl.exe, distrobox, toolbox, lxc or incus was found on PATH." : $"More than one is available; add --backend {string.Join("|", available.Select(b => b.Id))}.");
        var line = backend.CreateLine(name, args.Value("image"));
        if (output.Pickle.Shell.IsInteractive)
        {
            output.Pickle.Shell.SubmitCommand(line);
            return 0;
        }

        output.Line("Run: " + line);
        return 0;
    }

    private async Task<int> ExportAsync(CommandOutput output, DistroService service, List<string> rest, CancellationToken cancellationToken)
    {
        var args = CommandArgs.Parse(rest);
        if (args.Arg(0) is not { } name)
        {
            return UsageError(output, "Name the distribution to export.");
        }

        var wsl = Wsl(output, service);
        if (wsl is null)
        {
            return 1;
        }

        var file = args.Arg(1) ?? $"{DistroNames.Require(name)}-{DateTime.Now:yyyyMMdd}.tar";
        var full = Path.GetFullPath(file, output.Context.Cwd);
        if (File.Exists(full) && !output.Confirm(args, $"{full} exists. Replace it?", defaultYes: false))
        {
            output.Muted("Cancelled.");
            return 1;
        }

        return await StreamAsync(output, service, WslBackend.Export(name, full), $"Exported {name} to {full}.", cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> ImportAsync(CommandOutput output, DistroService service, List<string> rest, CancellationToken cancellationToken)
    {
        var args = CommandArgs.Parse(rest, "version");
        if (args.Positional.Count < 3)
        {
            return UsageError(output, "pk wsl import <name> <install directory> <tar file> [--version 1|2]");
        }

        if (Wsl(output, service) is null)
        {
            return 1;
        }

        int? version = args.Value("version") is { } v ? int.Parse(v, System.Globalization.CultureInfo.InvariantCulture) : null;
        var directory = Path.GetFullPath(args.Positional[1], output.Context.Cwd);
        var file = Path.GetFullPath(args.Positional[2], output.Context.Cwd);
        if (!File.Exists(file))
        {
            output.Failure($"{file} does not exist.");
            return 1;
        }

        return await StreamAsync(output, service, WslBackend.Import(args.Positional[0], directory, file, version), $"Imported {args.Positional[0]}.", cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> SetVersionAsync(CommandOutput output, DistroService service, List<string> rest, CancellationToken cancellationToken)
    {
        var args = CommandArgs.Parse(rest);
        if (args.Arg(0) is not { } name || args.Arg(1) is not { } version)
        {
            return UsageError(output, "pk wsl set-version <name> <1|2>");
        }

        if (Wsl(output, service) is null)
        {
            return 1;
        }

        return await StreamAsync(output, service, WslBackend.SetVersion(name, int.Parse(version, System.Globalization.CultureInfo.InvariantCulture)), $"{name} now uses WSL {version}.", cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> OnlineAsync(CommandOutput output, DistroService service, CancellationToken cancellationToken)
    {
        if (Wsl(output, service) is not { } wsl)
        {
            return 1;
        }

        foreach (var online in await wsl.ListOnlineAsync(cancellationToken).ConfigureAwait(false))
        {
            output.Object(Display.Columns(online, "Name", "Description"));
        }

        output.Muted("Install one: pk wsl new <Name>");
        return 0;
    }

    private async Task<int> ShutdownAsync(CommandOutput output, DistroService service, List<string> rest, CancellationToken cancellationToken)
    {
        var args = CommandArgs.Parse(rest);
        if (Wsl(output, service) is null)
        {
            return 1;
        }

        if (!output.Confirm(args, "Shut down WSL? Every running distribution stops and unsaved work in them is lost.", defaultYes: false))
        {
            output.Muted("Cancelled.");
            return 1;
        }

        return await StreamAsync(output, service, WslBackend.Shutdown, "WSL is shut down.", cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> StreamAsync(CommandOutput output, DistroService service, BackendCommand command, string done, CancellationToken cancellationToken)
    {
        var exit = await service.StreamAsync(command, line => output.Muted("  │ " + line.TrimEnd()), cancellationToken).ConfigureAwait(false);
        if (exit == 0)
        {
            output.Success(done);
            return 0;
        }

        output.Failure(exit == ProgramResult.NotFound ? $"{command.Program} was not found on PATH." : $"{command.Program} exited with {exit}.");
        return 1;
    }

    private static WslBackend? Wsl(CommandOutput output, DistroService service)
    {
        if (service.Backends.OfType<WslBackend>().FirstOrDefault(b => b.IsAvailable) is { } wsl)
        {
            return wsl;
        }

        output.Failure("WSL (wsl.exe) was not found on PATH.");
        return null;
    }

    private static async Task<Distro?> FindAsync(CommandOutput output, DistroService service, string target, CommandArgs args, CancellationToken cancellationToken)
    {
        var listing = await service.ListAsync(cancellationToken).ConfigureAwait(false);
        var distro = DistroService.Find(listing.Items, target, args.Value("backend"));
        if (distro is null)
        {
            output.Failure($"Nothing is called '{target}'. See: pk wsl list");
            foreach (var error in listing.Errors)
            {
                output.Warning(error);
            }
        }

        return distro;
    }

    private static int Report(CommandOutput output, ServiceOperationResult result)
    {
        if (result.Success)
        {
            output.Success(result.Message);
            return 0;
        }

        output.Failure(result.Message);
        return 1;
    }
}
