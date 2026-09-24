using System.Diagnostics;
using System.Globalization;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Tui.Widgets;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Pickle.Tui.Panels.SystemMonitoring;

/// <summary>
/// Network (Alt+N, <c>pk net</c>): Interfaces (status, addresses, live throughput with sparklines), Connections
/// (TCP connections and TCP/UDP listeners with their processes, type to filter) and Tools (ping, DNS lookup and, on
/// Windows, flushing the DNS cache). Left/Right switch tabs.
/// </summary>
public sealed class NetworkPanel : SystemPanelBase
{
    public const string PanelId = "network";

    internal static readonly TimeSpan InterfaceInterval = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan ConnectionInterval = TimeSpan.FromSeconds(3);
    private const int MaxLogLines = 1000;

    private readonly INetworkMonitor? _monitor;
    private readonly View _interfacesTab = new() { Title = "Interfaces", Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
    private readonly View _connectionsTab = new() { Title = "Connections", Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
    private readonly View _toolsTab = new() { Title = "Tools", Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
    private readonly SortableTable<NetworkInterfaceSample> _interfaces;
    private readonly Label _selected = new() { X = 0, Y = Pos.AnchorEnd(3), Width = Dim.Fill(), Height = 1 };
    private readonly MetersView _traffic = new() { X = 0, Y = Pos.AnchorEnd(2), Height = 2, LabelWidth = 8, ValueWidth = 14 };
    private readonly SortableTable<NetworkConnection> _connections;
    private readonly TextField _host = new() { X = 6, Y = 0, Width = 32 };
    private readonly PreviewPane _output = new("Output") { X = 0, Y = 2, Width = Dim.Fill(), Height = Dim.Fill() };
    private readonly Dictionary<string, (SampleHistory Send, SampleHistory Receive)> _history = new(StringComparer.Ordinal);
    private readonly List<string> _log = [];
    private CancellationTokenSource? _ping;
    private int _sampling;
    private int _loadingConnections;

    public NetworkPanel(PanelContext context)
        : base(context, "Network")
    {
        _monitor = Pickle.Services.Get<INetworkMonitor>();
        _interfaces = new SortableTable<NetworkInterfaceSample>(InterfaceColumns(), n => n.Id) { Height = Dim.Fill(3), Schemes = Schemes };
        _connections = new SortableTable<NetworkConnection>(
            ConnectionColumns(),
            c => (c.Protocol, c.LocalAddress, c.LocalPort, c.RemoteAddress, c.RemotePort),
            c => $"{c.Protocol} {Endpoint(c.LocalAddress, c.LocalPort)} {Endpoint(c.RemoteAddress, c.RemotePort)} {c.State} {c.ProcessName} {c.ProcessId}")
        {
            Schemes = Schemes,
        };
        _traffic.Schemes = Schemes;
        _output.Schemes = Schemes;
        if (_monitor is null)
        {
            Body.Add(new Label { X = 1, Y = 1, Text = "Network monitoring is not available in this session." });
            return;
        }

        _interfaces.SortBy(1, descending: false);
        _interfaces.SelectionChanged += (_, _) => ShowTraffic();
        _interfacesTab.Add(_interfaces, _selected, _traffic);

        _connections.SortBy(1, descending: false);
        _connectionsTab.Add(_connections);

        _host.Accepting += (_, e) =>
        {
            StartPing();
            e.Handled = true;
        };
        var ping = MakeButton("Ping", StartPing);
        ping.X = Pos.Right(_host) + 1;
        var stop = MakeButton("Stop", StopPing);
        stop.X = Pos.Right(ping) + 1;
        var lookup = MakeButton("DNS lookup", Lookup);
        lookup.X = Pos.Right(stop) + 1;
        _toolsTab.Add(new Label { X = 0, Y = 0, Text = "Host:" }, _host, ping, stop, lookup);
        if (_monitor.CanFlushDns)
        {
            var flush = MakeButton("Flush DNS cache", FlushDns);
            flush.X = Pos.Right(lookup) + 1;
            _toolsTab.Add(flush);
        }

        _toolsTab.Add(_output);
        _output.ShowMessage("Output", "Ping or look up a host name. Pinging continues until you stop it.");

        Body.Add(CreateTabs(_interfacesTab, _connectionsTab, _toolsTab));
        TabKeys(_interfaces.Table);
        TabKeys(_connections.Table);

        AddNote("←→ Tabs");
        AddHint(Key.F5, "Refresh", Refresh);
        AddHint(Key.F2, "Ping", StartPing);
        AddHint(Key.F3, "Stop", StopPing);
        AddHint(Key.F4, "DNS lookup", Lookup);
        if (_monitor.CanFlushDns)
        {
            AddHint(Key.F9, "Flush DNS", FlushDns);
        }

        Every(InterfaceInterval, RefreshInterfaces);
        Every(ConnectionInterval, () =>
        {
            if (CurrentTab == _connectionsTab)
            {
                RefreshConnections();
            }
        });
        _interfaces.Table.SetFocus();
    }

    internal SortableTable<NetworkInterfaceSample> Interfaces => _interfaces;

    internal SortableTable<NetworkConnection> Connections => _connections;

    internal MetersView Traffic => _traffic;

    internal View InterfacesTab => _interfacesTab;

    internal View ConnectionsTab => _connectionsTab;

    internal View ToolsTab => _toolsTab;

    internal string OutputText => string.Join('\n', _log);

    internal bool Pinging => _ping is not null;

    internal string Host
    {
        get => _host.Text ?? string.Empty;
        set => _host.Text = value;
    }

    internal static string Endpoint(string? address, int? port) => address is null
        ? string.Empty
        : (address.Contains(':', StringComparison.Ordinal) ? $"[{address}]" : address) + (port is { } p ? ":" + p.ToString(CultureInfo.InvariantCulture) : string.Empty);

    internal static string FormatReply(PingResult reply, int sequence) => reply.Success
        ? $"Reply from {reply.Address ?? "?"}: seq={sequence.ToString(CultureInfo.InvariantCulture)} time={reply.RoundTripMs.ToString(CultureInfo.InvariantCulture)} ms{(reply.Ttl is { } ttl ? " TTL=" + ttl.ToString(CultureInfo.InvariantCulture) : string.Empty)}"
        : $"seq={sequence.ToString(CultureInfo.InvariantCulture)}: {reply.Status}";

    internal void RefreshInterfaces()
    {
        if (_monitor is not { } monitor || Interlocked.Exchange(ref _sampling, 1) == 1)
        {
            return;
        }

        var token = Lifetime;
        _ = Task.Run(async () =>
        {
            try
            {
                var interfaces = await monitor.SampleInterfacesAsync(token).ConfigureAwait(false);
                OnUi(() => ApplyInterfaces(interfaces));
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Pickle.Log.Warn("network", "sampling interfaces failed", ex);
            }
            finally
            {
                Volatile.Write(ref _sampling, 0);
            }
        });
    }

    internal void RefreshConnections()
    {
        if (_monitor is not { } monitor || Interlocked.Exchange(ref _loadingConnections, 1) == 1)
        {
            return;
        }

        var token = Lifetime;
        _ = Task.Run(async () =>
        {
            try
            {
                var connections = await monitor.GetConnectionsAsync(token).ConfigureAwait(false);
                OnUi(() => _connections.SetItems(connections));
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Pickle.Log.Warn("network", "reading connections failed", ex);
            }
            finally
            {
                Volatile.Write(ref _loadingConnections, 0);
            }
        });
    }

    internal void ApplyInterfaces(IReadOnlyList<NetworkInterfaceSample> interfaces)
    {
        foreach (var nic in interfaces)
        {
            if (!_history.TryGetValue(nic.Id, out var history))
            {
                history = (new SampleHistory(), new SampleHistory());
                _history[nic.Id] = history;
            }

            history.Send.Add(nic.SendRate);
            history.Receive.Add(nic.ReceiveRate);
        }

        _interfaces.SetItems(interfaces);
        ShowTraffic();
    }

    internal void StartPing()
    {
        if (_monitor is not { } monitor)
        {
            return;
        }

        ShowTab(_toolsTab);
        if (ValidHost() is not { } host)
        {
            return;
        }

        StopPing();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(Lifetime);
        _ping = cts;
        Log($"PING {host}");
        _ = Task.Run(async () =>
        {
            var token = cts.Token;
            int sent = 0, received = 0;
            long min = long.MaxValue, max = 0, total = 0;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var started = Stopwatch.GetTimestamp();
                    var reply = await monitor.PingAsync(host, TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                    sent++;
                    if (reply.Success)
                    {
                        received++;
                        min = Math.Min(min, reply.RoundTripMs);
                        max = Math.Max(max, reply.RoundTripMs);
                        total += reply.RoundTripMs;
                    }

                    var line = FormatReply(reply, sent);
                    OnUi(() => Log(line));
                    var wait = TimeSpan.FromSeconds(1) - Stopwatch.GetElapsedTime(started);
                    if (wait > TimeSpan.Zero)
                    {
                        await Task.Delay(wait, token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                OnUi(() => Log("✗ " + ex.Message));
            }
            finally
            {
                var loss = sent == 0 ? 0 : (sent - received) * 100 / sent;
                var summary = $"--- {host}: {sent.ToString(CultureInfo.InvariantCulture)} sent, {received.ToString(CultureInfo.InvariantCulture)} received, {loss.ToString(CultureInfo.InvariantCulture)}% loss" +
                    (received > 0 ? $", min/avg/max {min.ToString(CultureInfo.InvariantCulture)}/{(total / received).ToString(CultureInfo.InvariantCulture)}/{max.ToString(CultureInfo.InvariantCulture)} ms" : string.Empty);
                OnUi(() =>
                {
                    Log(summary);
                    if (_ping == cts)
                    {
                        _ping = null;
                    }
                });
                cts.Dispose();
            }
        });
    }

    internal void StopPing()
    {
        try
        {
            _ping?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _ping = null;
    }

    internal void Lookup()
    {
        if (_monitor is not { } monitor)
        {
            return;
        }

        ShowTab(_toolsTab);
        if (ValidHost() is not { } host)
        {
            return;
        }

        Log($"DNS lookup {host}");
        RunInBackground(
            async ct =>
            {
                try
                {
                    var addresses = await monitor.LookupAsync(host, ct).ConfigureAwait(false);
                    return addresses.Count == 0 ? ["  (no addresses)"] : addresses.Select(a => "  " + a).ToList();
                }
                catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
                {
                    return new List<string> { "  ✗ " + ex.Message };
                }
            },
            lines => lines.ForEach(Log),
            "looking up…");
    }

    internal void FlushDns()
    {
        if (_monitor is not { CanFlushDns: true } monitor)
        {
            return;
        }

        ShowTab(_toolsTab);
        Log("Flushing the DNS resolver cache (ipconfig /flushdns)");
        RunInBackground(
            monitor.FlushDnsAsync,
            result =>
            {
                foreach (var line in result.Output.Split('\n'))
                {
                    Log((result.Success ? "  " : "  ✗ ") + line);
                }
            },
            "flushing DNS…");
    }

    protected override void OnOpened() => RefreshInterfaces();

    protected override void OnTabChanged(View page)
    {
        if (page == _connectionsTab)
        {
            RefreshConnections();
        }
        else if (page == _toolsTab)
        {
            _host.SetFocus();
        }
    }

    private static Button MakeButton(string text, Action action)
    {
        var button = new Button { Text = text, Y = 0 };
        button.Accepting += (_, e) =>
        {
            action();
            e.Handled = true;
        };
        return button;
    }

    private string? ValidHost()
    {
        var host = Host.Trim();
        if (host.Length is 0 or > 253 || host.StartsWith('-') || host.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            Log("Type a host name or IP address in the Host box first.");
            _host.SetFocus();
            return null;
        }

        return host;
    }

    private void Log(string line)
    {
        _log.Add(line);
        if (_log.Count > MaxLogLines)
        {
            _log.RemoveRange(0, _log.Count - MaxLogLines);
        }

        _output.Show("Output", _log);
        _output.ScrollBy(_log.Count);
    }

    private void Refresh()
    {
        RefreshInterfaces();
        if (CurrentTab == _connectionsTab)
        {
            RefreshConnections();
        }
    }

    private void ShowTraffic()
    {
        if (_interfaces.Selected is not { } nic || !_history.TryGetValue(nic.Id, out var history))
        {
            _selected.Text = string.Empty;
            _traffic.SetRows([]);
            return;
        }

        var details = new[] { nic.Description, nic.Mac, nic.IPv6.Count > 0 ? string.Join(", ", nic.IPv6) : null }
            .Where(s => !string.IsNullOrEmpty(s) && s != nic.Name);
        _selected.Text = $"{nic.Name}  ·  {string.Join("  ·  ", details)}";
        double Level(double rate) => nic.Speed is > 0 ? rate * 8 / nic.Speed.Value : 0;
        _traffic.SetRows(
        [
            new MeterRow("↑ Send", SystemFormat.Rate(nic.SendRate), history.Send.Values, null, Level(nic.SendRate)),
            new MeterRow("↓ Recv", SystemFormat.Rate(nic.ReceiveRate), history.Receive.Values, null, Level(nic.ReceiveRate)),
        ]);
    }

    private List<TableColumn<NetworkInterfaceSample>> InterfaceColumns() =>
    [
        new() { Header = "Name", Text = n => n.Name, MinWidth = 8, MaxWidth = 18 },
        new()
        {
            Header = "Status",
            Text = n => n.Status,
            SortKey = n => (n.Status == "Up" ? 0 : 1, n.Name),
            MinWidth = 6,
            MaxWidth = 14,
            Color = n => n.Status == "Up" ? Schemes.Success.Foreground : Schemes.Muted.Foreground,
        },
        new() { Header = "Type", Text = n => n.Type, MaxWidth = 14 },
        new() { Header = "IPv4", Text = n => string.Join(", ", n.IPv4), MinWidth = 4, MaxWidth = 20 },
        new() { Header = "↑ Send", Text = n => SystemFormat.Rate(n.SendRate), SortKey = n => n.SendRate, Numeric = true, MinWidth = 11, MaxWidth = 11 },
        new() { Header = "↓ Receive", Text = n => SystemFormat.Rate(n.ReceiveRate), SortKey = n => n.ReceiveRate, Numeric = true, MinWidth = 11, MaxWidth = 11 },
        new() { Header = "Speed", Text = n => SystemFormat.Bits(n.Speed), SortKey = n => n.Speed ?? 0, Numeric = true, MinWidth = 5, MaxWidth = 10 },
    ];

    private static List<TableColumn<NetworkConnection>> ConnectionColumns() =>
    [
        new() { Header = "Proto", Text = c => c.Protocol, MinWidth = 5, MaxWidth = 5 },
        new() { Header = "Local", Text = c => Endpoint(c.LocalAddress, c.LocalPort), SortKey = c => (c.LocalPort, c.LocalAddress), MinWidth = 10, MaxWidth = 30 },
        new() { Header = "Remote", Text = c => Endpoint(c.RemoteAddress, c.RemotePort), MinWidth = 6, MaxWidth = 30 },
        new() { Header = "State", Text = c => c.State, MinWidth = 5, MaxWidth = 11 },
        new() { Header = "PID", Text = c => c.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, SortKey = c => c.ProcessId ?? -1, Numeric = true, MinWidth = 5, MaxWidth = 7 },
        new() { Header = "Process", Text = c => c.ProcessName ?? string.Empty },
    ];
}
