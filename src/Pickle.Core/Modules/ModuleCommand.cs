using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Core.Modules;

/// <summary><c>pk module</c>: the optional modules (Docker, Kubernetes, nmap, …) and which are turned on.</summary>
public sealed class ModuleCommand(PickleRuntime runtime) : PickleCommandBase
{
    public override string Name => "module";

    public override string Description => "List, enable, disable and choose optional modules (Docker, Kubernetes, nmap, …)";

    public override string Usage => "pk module [list [--all] | info <id> | enable <id>... | disable <id>... | setup]";

    public override IReadOnlyList<string> Examples =>
    [
        "pk module list                     what is available and what is on",
        "pk module enable docker nmap       turn modules on (they load immediately)",
        "pk module disable kubernetes       turn one off (it unloads at the next start)",
        "pk module setup                    pick modules from a checklist",
    ];

    private ModuleCatalog Catalog => runtime.ModuleCatalog;

    protected override Task<int> RunAsync(CommandOutput output, IReadOnlyList<string> raw, CancellationToken cancellationToken)
    {
        var args = CommandArgs.Parse(raw);
        var sub = args.Arg(0)?.ToLowerInvariant() ?? "list";
        var rest = args.Positional.Skip(1).ToList();
        return Task.FromResult(sub switch
        {
            "list" or "ls" => List(output, args.Has("all")),
            "info" or "show" => rest.Count == 1 ? Info(output, rest[0]) : UsageError(output, "Name one module."),
            "enable" or "on" or "add" => rest.Count > 0 ? Change(output, rest, enable: true) : UsageError(output, "Name the modules to enable."),
            "disable" or "off" or "remove" => rest.Count > 0 ? Change(output, rest, enable: false) : UsageError(output, "Name the modules to disable."),
            "setup" or "pick" => Setup(output),
            _ => UsageError(output, $"Unknown subcommand '{sub}'."),
        });
    }

    private int List(CommandOutput output, bool all)
    {
        foreach (var status in Catalog.Status().Where(s => all || !s.Module.Hidden))
        {
            output.Object(Display.Columns(
                new ModuleRow(status.Module.Id, status.Module.Name, Label(status), status.Source, status.Module.Description, status.Module.Platforms.ToString()),
                "Id", "Name", "State", "Source"));
        }

        output.Muted("Turn modules on with 'pk module enable <id>' or pick from a list with 'pk module setup'.");
        return 0;
    }

    private int Info(CommandOutput output, string id)
    {
        if (Catalog.Find(id) is not { } status)
        {
            return UsageError(output, $"There is no module called '{id}'.");
        }

        var m = status.Module;
        output.Heading($"{m.Name} ({m.Id})");
        output.Line(m.Description);
        output.Line($"State: {Label(status)}" + (status.Error is null ? string.Empty : " — " + status.Error));
        output.Line($"Platforms: {m.Platforms}");
        if (m.Tools.Count > 0)
        {
            output.Line("Uses: " + string.Join(", ", m.Tools) + " (installed separately; 'pk tool install <name>' on Windows)");
        }

        if (m.Provides.Count > 0)
        {
            output.Line("Adds: " + string.Join(", ", m.Provides));
        }

        return 0;
    }

    private int Change(CommandOutput output, IReadOnlyList<string> ids, bool enable)
    {
        var failed = false;
        foreach (var id in ids)
        {
            if (Catalog.Find(id) is null)
            {
                output.Failure($"There is no module called '{id}'.");
                failed = true;
                continue;
            }

            var status = enable ? Catalog.Enable(id) : Catalog.Disable(id);
            if (enable)
            {
                switch (status.State)
                {
                    case ModuleState.Loaded:
                        output.Success($"{status.Module.Name} is on.");
                        break;
                    case ModuleState.Unsupported:
                        output.Warning($"{status.Module.Name} is on, but it is not available on this operating system.");
                        break;
                    default:
                        output.Failure($"{status.Module.Name} could not start: {status.Error ?? status.State.ToString()}");
                        failed = true;
                        break;
                }
            }
            else
            {
                output.Success($"{status.Module.Name} is off" + (status.State == ModuleState.Loaded ? " (it unloads when Pickle restarts)." : "."));
            }
        }

        return failed ? 1 : 0;
    }

    private int Setup(CommandOutput output)
    {
        var picked = ModulePicker.Run(runtime.Terminal, runtime.Themes.Current.Ui, [.. Catalog.Status().Where(s => !s.Module.Hidden)]);
        if (picked is null)
        {
            output.Muted(runtime.Terminal.IsInteractive ? "No changes." : "Not an interactive terminal; use 'pk module enable <id>'.");
            return runtime.Terminal.IsInteractive ? 0 : 1;
        }

        Catalog.Select(picked);
        output.Success(picked.Count == 0 ? "No optional modules are on." : $"On: {string.Join(", ", picked.Order(StringComparer.OrdinalIgnoreCase))}.");
        return 0;
    }

    internal static string Label(ModuleStatus status) => status.State switch
    {
        ModuleState.Loaded when !status.Enabled => "on until restart",
        ModuleState.Loaded => "on",
        ModuleState.Pending => "on (loads at next start)",
        ModuleState.Unsupported => "not on this OS",
        ModuleState.Failed => "failed",
        _ => "off",
    };

    public sealed record ModuleRow(string Id, string Name, string State, string Source, string Description, string Platforms);
}
