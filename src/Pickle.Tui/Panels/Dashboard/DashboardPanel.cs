using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Terminal.Gui.Input;

namespace Pickle.Tui.Panels.Dashboard;

/// <summary>
/// Dashboard (Alt+I, <c>pk dashboard</c>): the machine at a glance — system facts, CPU and memory with history,
/// network throughput and addresses, disk space and the busiest processes — refreshed every 1.5 s.
/// </summary>
public sealed class DashboardPanel : PanelWindow
{
    public const string PanelId = "dashboard";

    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(1500);

    private readonly DashboardSampler _sampler;
    private readonly CanvasView _view;
    private DashboardData? _data;
    private int _sampling;

    public DashboardPanel(PanelContext context)
        : base(context, "Dashboard")
    {
        _sampler = new DashboardSampler(Pickle);
        _view = new CanvasView((width, height) => _data is { } data ? DashboardLayout.Render(data, width, height) : null) { Schemes = Schemes };
        Body.Add(_view);
        AddHint(Key.F5, "Refresh", Refresh);
        AddHint(Key.P.WithAlt, "Processes", () => OpenPanel("processes"));
        AddHint(Key.N.WithAlt, "Network", () => OpenPanel("network"));
        AddHint(Key.D.WithAlt, "Disks", () => OpenPanel("disks"));
        Every(RefreshInterval, Refresh);
        _view.SetFocus();
    }

    internal DashboardData? Latest => _data;

    internal CanvasView Canvas => _view;

    internal void Apply(DashboardData data)
    {
        _data = data;
        _view.SetNeedsDraw();
    }

    internal void Refresh()
    {
        if (Interlocked.Exchange(ref _sampling, 1) == 1)
        {
            return;
        }

        var token = Lifetime;
        _ = Task.Run(async () =>
        {
            try
            {
                var data = await _sampler.SampleAsync(token).ConfigureAwait(false);
                OnUi(() => Apply(data));
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Pickle.Log.Warn("dashboard", "sampling failed", ex);
            }
            finally
            {
                Volatile.Write(ref _sampling, 0);
            }
        });
    }

    protected override void OnOpened() => Refresh();
}

/// <summary><c>pk dashboard [--once]</c>: opens the dashboard, or prints one snapshot (also when not interactive).</summary>
public sealed class DashboardCommand : PickleCommandBase
{
    public override string Name => "dashboard";

    public override string Description => "System dashboard: CPU, memory, disks, network, uptime, battery, busiest processes";

    public override string Usage => "pk dashboard [--once]";

    protected override async Task<int> RunAsync(CommandOutput output, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var once = args.Any(a => a is "--once" or "-1" or "once");
        if (args.FirstOrDefault(a => !(a is "--once" or "-1" or "once")) is { } unknown)
        {
            return UsageError(output, $"Unknown argument '{unknown}'.");
        }

        var pickle = output.Pickle;
        if (!once && pickle.Services.Get<IPanelHost>() is { } host && pickle.Shell.IsInteractive)
        {
            host.Show(DashboardPanel.PanelId);
            return 0;
        }

        // Rates and CPU % are measured between two samples.
        var sampler = new DashboardSampler(pickle);
        await sampler.SampleAsync(cancellationToken).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromMilliseconds(700), cancellationToken).ConfigureAwait(false);
        var data = await sampler.SampleAsync(cancellationToken).ConfigureAwait(false);
        var width = Math.Clamp(await output.WidthAsync().ConfigureAwait(false), 40, 160);
        var canvas = DashboardLayout.Render(data, width, DashboardLayout.NaturalHeight(data, width));
        foreach (var line in canvas.ToAnsi(pickle.Themes.Current).TrimEnd('\n').Split('\n'))
        {
            output.Line(line);
        }

        return 0;
    }
}
