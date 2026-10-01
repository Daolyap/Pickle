using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Terminal.Gui.Input;

namespace Pickle.Tui.Panels.Admin;

/// <summary>
/// Services and daemons (Alt+V): Windows services, systemd units (system and per-user) or launchd jobs. Start, stop,
/// restart and enable/disable run as one privileged command (UAC on Windows, sudo or polkit elsewhere); logs open in the shell.
/// </summary>
internal sealed class ServicesPanel : ResourcePanel<ServiceInfo>
{
    public const string PanelId = "services";

    private readonly ISystemServiceManager _manager;
    private bool _userScope;

    public static PanelDescriptor Descriptor { get; } = new()
    {
        Id = PanelId,
        Title = "Services",
        Description = "Windows services, systemd units and launchd jobs: start, stop, restart, startup type, logs",
        DefaultKey = "Alt+V",
        CreateView = context => new ServicesPanel(context),
    };

    public ServicesPanel(PanelContext context)
        : base(context, "Services", s => s.Id, "Service")
    {
        _manager = context.Pickle.Services.Require<ISystemServiceManager>();
        PanelTitle = $"Services ({_manager.Name})";
        if (!string.IsNullOrEmpty(context.Argument))
        {
            List.FilterText = context.Argument;
        }

        AddAction(Key.F2, "Start", (s, ct) => Control(s, ServiceAction.Start, ct), enabled: s => !s.IsRunning);
        AddAction(Key.F3, "Stop", (s, ct) => Control(s, ServiceAction.Stop, ct), confirm: s => $"Stop {s.Id}?", enabled: s => s.IsRunning);
        AddAction(Key.F4, "Restart", (s, ct) => Control(s, ServiceAction.Restart, ct), confirm: s => $"Restart {s.Id}?");
        AddAction(Key.F6, "Start at boot", (s, ct) => Control(s, ServiceAction.Enable, ct));
        AddAction(Key.F7, "Not at boot", (s, ct) => Control(s, ServiceAction.Disable, ct), confirm: s => $"Stop {s.Id} from starting at boot?");
        AddCommand(Key.F8, "Logs", s => _manager.LogsCommand(s.Id, _userScope));
        if (_manager.HasUserScope)
        {
            AddHint(Key.F9, "System/User", ToggleScope);
        }
    }

    protected override TimeSpan? AutoRefresh => TimeSpan.FromSeconds(15);

    protected override string EmptyMessage => _manager.IsSupported ? "No services found." : _manager.Name + " is not available on this machine.";

    protected override Task<IReadOnlyList<ServiceInfo>> LoadAsync(CancellationToken cancellationToken) => _manager.ListAsync(_userScope, cancellationToken);

    protected override Task<IReadOnlyList<string>> DescribeAsync(ServiceInfo item, CancellationToken cancellationToken) =>
        _manager.DetailsAsync(item.Id, _userScope, cancellationToken);

    protected override string? Hint(ServiceInfo item) => $"{item.State} · {item.StartMode}";

    protected override string? Detail(ServiceInfo item) => item.DisplayName == item.Id ? null : item.DisplayName;

    protected override string? Category(ServiceInfo item) => item.State switch
    {
        ServiceState.Failed => "Failed",
        ServiceState.Running or ServiceState.Starting => "Running",
        _ => "Stopped",
    };

    protected override Terminal.Gui.Drawing.Color? ItemColor(ServiceInfo item) => item.State switch
    {
        ServiceState.Failed => Schemes.ErrorText.Foreground,
        ServiceState.Running or ServiceState.Starting => Schemes.Success.Foreground,
        _ => Schemes.Muted.Foreground,
    };

    protected override string KeyOf(ServiceInfo item) => item.Id;

    private async Task<ActionOutcome> Control(ServiceInfo service, ServiceAction action, CancellationToken cancellationToken)
    {
        var result = await _manager.ControlAsync(service.Id, action, _userScope, cancellationToken).ConfigureAwait(false);
        return result.Success ? ActionOutcome.Done(result.Message) : new ActionOutcome(false, result.Message) { ShellCommand = result.ShellCommand };
    }

    private void ToggleScope()
    {
        _userScope = !_userScope;
        PanelTitle = $"Services ({_manager.Name}, {(_userScope ? "user" : "system")})";
        Reload();
    }
}
