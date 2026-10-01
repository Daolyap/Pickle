using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Tui;
using Terminal.Gui.Input;

namespace Pickle.Modules.Nmap;

internal sealed record NmapRow(string Text, string? Detail, string? State, NmapHost Host, NmapPort? Port);

/// <summary>nmap (Alt+Shift+N): pick a target and a profile, scan in the background, browse hosts and open ports. F4 hands the equivalent command to the shell (to run it with sudo).</summary>
internal sealed class NmapPanel : ResourcePanel<NmapRow>
{
    public const string PanelId = "nmap";

    private readonly NmapService _nmap;
    private string? _target;
    private NmapProfile _profile = NmapService.Profiles[0];
    private string _summary = string.Empty;

    public static PanelDescriptor Descriptor { get; } = new()
    {
        Id = PanelId,
        Title = "nmap",
        Description = "Scan hosts and ports with nmap: profiles, results as a browsable list",
        DefaultKey = "Alt+Shift+N",
        CreateView = context => new NmapPanel(context),
    };

    public NmapPanel(PanelContext context)
        : base(context, "nmap", r => r.Text, "Details")
    {
        _nmap = new NmapService(context.Pickle.Services.Require<IProgramRunner>());
        AddHint(Key.F2, "New scan", AskTarget);
        AddHint(Key.F3, "Profile", PickProfile);
        AddCommand(Key.F4, "Run in shell", _ => _target is null ? null : PowerShellQuote.Command("nmap", [.. NmapService.Arguments(_target, _profile, null, xml: false).Where(a => a != "-oX" && a != "-")]));
        if (!string.IsNullOrWhiteSpace(context.Argument))
        {
            _target = context.Argument.Trim();
            NmapService.Targets(_target);
        }

        Retitle();
    }

    protected override string EmptyMessage => !_nmap.IsAvailable
        ? "nmap is not installed. Windows: pk tool install nmap. Linux/macOS: use your package manager."
        : _target is null ? "F2 starts a scan." : $"{_summary}\nNo hosts answered.";

    internal string? Target => _target;

    internal string Summary => _summary;

    protected override async Task<IReadOnlyList<NmapRow>> LoadAsync(CancellationToken cancellationToken)
    {
        if (_target is null)
        {
            return [];
        }

        var scan = await _nmap.ScanAsync(_target, _profile, null, cancellationToken).ConfigureAwait(false);
        _summary = scan.Summary;
        OnUi(Retitle);
        var rows = new List<NmapRow>();
        foreach (var host in scan.Hosts.Where(h => h.Status == "up"))
        {
            var open = host.OpenPorts.ToList();
            rows.Add(new NmapRow(host.Hostname is { } name ? $"{host.Address}  ({name})" : host.Address, $"{open.Count} open · {host.Os ?? host.Vendor ?? string.Empty}".TrimEnd(' ', '·'), "host", host, null));
            rows.AddRange(open.Select(p => new NmapRow($"    {p.Port}/{p.Protocol} {p.Service}", p.Version, p.State, host, p)));
        }

        return rows;
    }

    protected override Task<IReadOnlyList<string>> DescribeAsync(NmapRow item, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        if (item.Port is { } port)
        {
            lines.Add($"{item.Host.Address}:{port.Port}/{port.Protocol}");
            lines.Add($"State:   {port.State}");
            lines.Add($"Service: {port.Service} {port.Version}".TrimEnd());
            foreach (var script in port.Scripts)
            {
                lines.Add(string.Empty);
                lines.Add(script.Id + ":");
                lines.AddRange(script.Output.Split('\n').Select(l => "  " + l.Trim()));
            }
        }
        else
        {
            var host = item.Host;
            lines.Add($"Host:     {host.Address}{(host.Hostname is { } name ? " (" + name + ")" : string.Empty)}");
            lines.Add($"State:    {host.Status}");
            lines.Add($"MAC:      {host.Mac ?? "—"}{(host.Vendor is { } vendor ? " (" + vendor + ")" : string.Empty)}");
            lines.Add($"OS guess: {host.Os ?? "—"}");
            lines.Add($"Open:     {string.Join(", ", host.OpenPorts.Select(p => $"{p.Port}/{p.Protocol}"))}");
        }

        return Task.FromResult<IReadOnlyList<string>>(lines);
    }

    protected override string KeyOf(NmapRow item) => item.Host.Address + "|" + item.Port?.Port;

    protected override string? Hint(NmapRow item) => item.State;

    protected override string? Detail(NmapRow item) => item.Detail;

    protected override Terminal.Gui.Drawing.Color? ItemColor(NmapRow item) => item.Port is null ? Schemes.Accent.Foreground : null;

    private void AskTarget()
    {
        if (Prompt("New scan", "target (host, address, 10.0.0.0/24, 10.0.0.1-50)", _target ?? string.Empty) is not { Length: > 0 } target)
        {
            return;
        }

        IReadOnlyList<string> targets;
        try
        {
            targets = NmapService.Targets(target);
        }
        catch (ArgumentException ex)
        {
            ShowError(ex.Message);
            return;
        }

        if (!NmapService.IsLocalOnly(targets)
            && !Confirm("Authorization", "Only scan systems you own or have written permission to test.\n\nThis target is not on a private network. Scan it?"))
        {
            return;
        }

        _target = string.Join(' ', targets);
        Retitle();
        Reload();
    }

    private void PickProfile()
    {
        if (Pick("Scan profile", NmapService.Profiles, p => p.Name, p => p.Description) is { } profile)
        {
            _profile = profile;
            Retitle();
            if (_target is not null)
            {
                Reload();
            }
        }
    }

    private void Retitle() => PanelTitle = $"nmap · {_profile.Name}{(_target is null ? string.Empty : " · " + _target)}{(_summary.Length > 0 ? " · " + _summary : string.Empty)}";
}
