using System.Diagnostics;
using Pickle.Abstractions;
using Pickle.Tui.Panels.Dashboard;
using Terminal.Gui.Input;

namespace Pickle.Tui.Panels.Devious;

/// <summary>
/// <c>pk devious</c>: Pickle's take on hollywood. A wall of busy panes — processes, connections, hex dumps of real
/// binaries, your project's code typed out, hashes, logs, git history, throughput — all real data from this machine,
/// rearranged every 15 s. Space reshuffles, Esc or q leaves. Read-only: nothing is changed or sent anywhere.
/// </summary>
public sealed class DeviousPanel : PanelWindow
{
    public const string PanelId = "devious";

    internal static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(80);

    private readonly DeviousSources _sources;
    private readonly DeviousScreen _screen;
    private readonly CanvasView _view;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _last;

    public DeviousPanel(PanelContext context)
        : base(context, "devious")
    {
        var seed = Environment.TickCount;
        _sources = new DeviousSources(Pickle, Pickle.Shell.CurrentDirectory, seed);
        _screen = new DeviousScreen(_sources, seed);
        _view = new CanvasView(_screen.Render) { Schemes = Schemes };
        Body.Add(_view);
        AddHint(Key.Space, "Shuffle", _screen.Shuffle);
        KeyDown += (_, key) =>
        {
            if (key == Key.Q || key == Key.C.WithCtrl)
            {
                Close();
                key.Handled = true;
            }
        };
        Every(FrameInterval, NextFrame);
        _view.SetFocus();
    }

    internal DeviousScreen Screen => _screen;

    internal DeviousSources Sources => _sources;

    internal CanvasView Canvas => _view;

    internal void NextFrame()
    {
        var now = _clock.Elapsed;
        _screen.Advance(now - _last);
        _last = now;
        _view.SetNeedsDraw();
    }

    protected override void OnOpened() => _sources.Start(Lifetime);
}

/// <summary><c>pk devious</c> opens it.</summary>
public sealed class DeviousCommand : IPickleCommand
{
    public string Name => "devious";

    public string Description => "Look extremely busy: hollywood-style panes of this machine's real processes, traffic, code, hashes and logs";

    public string Usage => "pk devious";

    public ValueTask<int> ExecuteAsync(PickleCommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (context.Pickle.Services.Get<IPanelHost>() is not { } host || !context.Pickle.Shell.IsInteractive)
        {
            context.WriteError("pk devious needs an interactive terminal.");
            return ValueTask.FromResult(1);
        }

        host.Show(DeviousPanel.PanelId);
        return ValueTask.FromResult(0);
    }
}
