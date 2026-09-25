using Pickle.Abstractions;

// The namespace avoids a "System" segment: Pickle.Tui.Panels.System would shadow the BCL System namespace in the
// sibling panel namespaces (e.g. System.Security.SecurityException in Panels/Files).
namespace Pickle.Tui.Panels.SystemMonitoring;

/// <summary>
/// Registers the Processes (Alt+P, <c>pk top</c>), Network (Alt+N, <c>pk net</c>) and Disks (Alt+D, <c>pk disks</c>)
/// panels. Their data comes from the monitors registered by Pickle.Core's SystemMonitorsPlugin.
/// </summary>
public sealed class SystemPanelsPlugin : IPicklePlugin
{
    public string Id => "pickle.system.panels";

    public string DisplayName => "System panels";

    public string Description => "Task manager, network and disk monitors.";

    public void Initialize(IPickleContext context)
    {
        context.Panels.Register(new PanelDescriptor
        {
            Id = ProcessesPanel.PanelId,
            Title = "Processes",
            Description = "Task manager: CPU, memory, processes; end, priority, details",
            DefaultKey = "Alt+P",
            CreateView = ctx => new ProcessesPanel(ctx),
        });
        context.Panels.Register(new PanelDescriptor
        {
            Id = NetworkPanel.PanelId,
            Title = "Network",
            Description = "Interfaces and throughput, connections and listeners, ping, DNS lookup",
            DefaultKey = "Alt+N",
            CreateView = ctx => new NetworkPanel(ctx),
        });
        context.Panels.Register(new PanelDescriptor
        {
            Id = DisksPanel.PanelId,
            Title = "Disks",
            Description = "Volumes, disk I/O and a folder size analyzer",
            DefaultKey = "Alt+D",
            CreateView = ctx => new DisksPanel(ctx),
        });

        context.Commands.Register(new OpenPanelCommand("top", ProcessesPanel.PanelId, "Open the task manager (processes, CPU, memory)", "pk top [filter]"));
        context.Commands.Register(new OpenPanelCommand("net", NetworkPanel.PanelId, "Open the network monitor (interfaces, connections, ping, DNS)", "pk net"));
        context.Commands.Register(new OpenPanelCommand("disks", DisksPanel.PanelId, "Open the disk monitor, or analyze a folder's disk usage", "pk disks [folder]", argumentIsPath: true));
    }
}

/// <summary><c>pk &lt;name&gt; [argument]</c> opens a panel (once the prompt is back when run from a pipeline).</summary>
public sealed class OpenPanelCommand(string name, string panelId, string description, string usage, bool argumentIsPath = false) : IPickleCommand
{
    public string Name => name;

    public string Description => description;

    public string Usage => usage;

    public ValueTask<int> ExecuteAsync(PickleCommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (context.Pickle.Services.Get<IPanelHost>() is not { } host)
        {
            context.WriteError("Panels are not available in this session.");
            return ValueTask.FromResult(1);
        }

        string? argument = args.Count == 0 ? null : string.Join(' ', args);
        if (argument is not null && argumentIsPath)
        {
            argument = ResolvePath(argument, context.Cwd);
            if (!Directory.Exists(argument))
            {
                context.WriteError($"Folder not found: {argument}");
                return ValueTask.FromResult(1);
            }
        }

        host.Show(panelId, argument);
        return ValueTask.FromResult(0);
    }

    private static string ResolvePath(string path, string cwd)
    {
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            path = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[1..]);
        }

        return Path.GetFullPath(path, cwd);
    }
}
