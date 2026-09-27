using Pickle.Tui.Panels.Dashboard;

namespace Pickle.Tui.Panels.Devious;

/// <summary>
/// Splits the screen into a random arrangement of panes (like hollywood's tmux splits), picks different panes for
/// them, animates them, and reshuffles every <see cref="ShuffleInterval"/> or on request.
/// </summary>
internal sealed class DeviousScreen
{
    public static readonly TimeSpan ShuffleInterval = TimeSpan.FromSeconds(15);

    private const int MinWidth = 26;
    private const int MinHeight = 7;

    private readonly IReadOnlyList<DeviousPane> _all;
    private readonly Random _random;
    private readonly List<(Area Box, DeviousPane Pane)> _layout = [];
    private (int Width, int Height) _size;
    private TimeSpan _sinceShuffle;
    private bool _shuffle = true;

    public DeviousScreen(DeviousSources sources, int seed)
    {
        _random = new Random(seed);
        _all =
        [
            new TopPane(sources), new HexPane(sources), new CodePane(sources), new NetstatPane(sources), new TrafficPane(sources),
            new MetersPane(sources), new TreePane(sources), new HashPane(sources), new LogPane(sources), new GitPane(sources),
            new SystemPane(sources), new MatrixPane(sources, seed),
        ];
    }

    /// <summary>The panes on screen now, with their boxes (tests).</summary>
    public IReadOnlyList<(Area Box, DeviousPane Pane)> Layout => _layout;

    public void Shuffle() => _shuffle = true;

    public void Advance(TimeSpan elapsed)
    {
        _sinceShuffle += elapsed;
        if (_sinceShuffle >= ShuffleInterval)
        {
            _shuffle = true;
        }

        foreach (var pane in _all)
        {
            pane.Tick(elapsed);
        }
    }

    public DashboardCanvas Render(int width, int height)
    {
        var canvas = new DashboardCanvas(width, height);
        if (_shuffle || _size != (width, height))
        {
            Arrange(width, height);
        }

        foreach (var (box, pane) in _layout)
        {
            canvas.Box(box.X, box.Y, box.Width, box.Height, pane.Title);
            pane.Draw(canvas, new Area(box.X + 2, box.Y + 1, Math.Max(0, box.Width - 4), Math.Max(0, box.Height - 2)));
        }

        return canvas;
    }

    private void Arrange(int width, int height)
    {
        (_size, _shuffle, _sinceShuffle) = ((width, height), false, TimeSpan.Zero);
        _layout.Clear();
        var available = _all.Where(p => p.Available).OrderBy(_ => _random.Next()).ToList();
        var wanted = Math.Clamp(width * height / 900, 2, 8);
        var boxes = Split(new Area(0, 0, width, height), Math.Min(wanted, available.Count));
        for (var i = 0; i < boxes.Count && i < available.Count; i++)
        {
            _layout.Add((boxes[i], available[i]));
        }
    }

    /// <summary>Repeatedly halves the biggest box (across its long side, cells being about twice as tall as wide).</summary>
    private List<Area> Split(Area screen, int count)
    {
        var boxes = new List<Area> { screen };
        while (boxes.Count < count)
        {
            var candidates = boxes.Where(b => b.Width >= MinWidth * 2 || b.Height >= MinHeight * 2).OrderByDescending(b => b.Width * b.Height).ToList();
            if (candidates.Count == 0)
            {
                break;
            }

            var box = candidates[0];
            boxes.Remove(box);
            var ratio = 0.35 + (_random.NextDouble() * 0.3);
            var sideBySide = box.Width >= MinWidth * 2 && (box.Width > box.Height * 2.5 || box.Height < MinHeight * 2);
            if (sideBySide)
            {
                var left = Math.Clamp((int)(box.Width * ratio), MinWidth, box.Width - MinWidth);
                boxes.Add(box with { Width = left });
                boxes.Add(box with { X = box.X + left, Width = box.Width - left });
            }
            else
            {
                var top = Math.Clamp((int)(box.Height * ratio), MinHeight, box.Height - MinHeight);
                boxes.Add(box with { Height = top });
                boxes.Add(box with { Y = box.Y + top, Height = box.Height - top });
            }
        }

        return [.. boxes.OrderBy(b => b.Y).ThenBy(b => b.X)];
    }
}
