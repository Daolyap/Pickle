using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Pickle.Abstractions.Services;
using Pickle.Tui.Panels.Dashboard;
using Pickle.Tui.Panels.SystemMonitoring;

namespace Pickle.Tui.Panels.Devious;

/// <summary>The inside of a pane's box.</summary>
internal readonly record struct Area(int X, int Y, int Width, int Height);

/// <summary>One animated pane of <c>pk devious</c>. <see cref="Tick"/> advances it; <see cref="Draw"/> paints the box's inside.</summary>
internal abstract class DeviousPane(DeviousSources sources)
{
    protected DeviousSources Sources { get; } = sources;

    public abstract string Title { get; }

    /// <summary>False when its data isn't there (no repository, no logs found, …); the layout picks another pane.</summary>
    public virtual bool Available => true;

    public virtual void Tick(TimeSpan elapsed)
    {
    }

    public abstract void Draw(DashboardCanvas canvas, Area area);

    protected static int Steps(ref double accumulator, TimeSpan elapsed, double perSecond)
    {
        accumulator += elapsed.TotalSeconds * perSecond;
        var steps = (int)accumulator;
        accumulator -= steps;
        return steps;
    }
}

/// <summary>"top": the real process table by CPU, with a cursor wandering over it.</summary>
internal sealed class TopPane(DeviousSources sources) : DeviousPane(sources)
{
    private double _cursorClock;
    private int _cursor;

    public override string Title => "top";

    public override void Tick(TimeSpan elapsed) => _cursor += Steps(ref _cursorClock, elapsed, 1.5);

    public override void Draw(DashboardCanvas c, Area a)
    {
        if (Sources.Processes is not { } snapshot)
        {
            c.Text(a.X, a.Y, "sampling processes…", CellRole.Muted);
            return;
        }

        c.Text(a.X, a.Y, $"cpu {SystemFormat.Percent(snapshot.CpuPercent)}  mem {SystemFormat.Bytes(snapshot.MemoryUsed)}/{SystemFormat.Bytes(snapshot.MemoryTotal)}  tasks {snapshot.Processes.Count}", CellRole.Accent, a.Width);
        c.Text(a.X, a.Y + 1, Row("PID", "CPU%", "MEM", "COMMAND", a.Width), CellRole.Title, a.Width);
        var rows = snapshot.Processes.OrderByDescending(p => p.CpuPercent).ThenByDescending(p => p.WorkingSet).Take(Math.Max(0, a.Height - 2)).ToList();
        for (var i = 0; i < rows.Count; i++)
        {
            var p = rows[i];
            var role = rows.Count > 0 && i == _cursor % rows.Count ? CellRole.Accent : p.CpuPercent >= 10 ? CellRole.Warning : CellRole.Normal;
            c.Text(a.X, a.Y + 2 + i, Row(p.Id.ToString(CultureInfo.InvariantCulture), p.CpuPercent.ToString("0.0", CultureInfo.InvariantCulture), SystemFormat.Bytes(p.WorkingSet), p.Name, a.Width), role, a.Width);
        }
    }

    private static string Row(string pid, string cpu, string memory, string name, int width) =>
        $"{pid,7} {cpu,5} {memory,9}  {name}".PadRight(width);
}

/// <summary>"xxd": scrolls through slices of real binaries (System32 DLLs, /usr/bin, Pickle itself).</summary>
internal sealed class HexPane(DeviousSources sources) : DeviousPane(sources)
{
    private DeviousBytes? _bytes;
    private double _clock;
    private int _line;

    public override string Title => "xxd " + (_bytes?.Name ?? string.Empty);

    public override bool Available => Sources.HasBinaries;

    public override void Tick(TimeSpan elapsed)
    {
        _line += Steps(ref _clock, elapsed, 18);
        if (_bytes is null || _line * 16 >= _bytes.Bytes.Length)
        {
            _bytes = Sources.NextBytes() ?? _bytes;
            _line = 0;
        }
    }

    public override void Draw(DashboardCanvas c, Area a)
    {
        if (_bytes is not { } data)
        {
            return;
        }

        var first = Math.Max(0, _line - a.Height + 1);
        for (var row = 0; row < a.Height && first + row <= _line; row++)
        {
            var start = (first + row) * 16;
            if (start >= data.Bytes.Length)
            {
                break;
            }

            var chunk = data.Bytes.AsSpan(start, Math.Min(16, data.Bytes.Length - start));
            var offset = (data.Offset + start).ToString("x8", CultureInfo.InvariantCulture);
            var x = a.X + c.Text(a.X, a.Y + row, offset + ": ", CellRole.Muted, a.Width);
            var hex = new StringBuilder();
            for (var i = 0; i < 16; i++)
            {
                hex.Append(i < chunk.Length ? chunk[i].ToString("x2", CultureInfo.InvariantCulture) : "  ").Append(i % 2 == 1 ? " " : string.Empty);
            }

            x += c.Text(x, a.Y + row, hex.ToString(), row == a.Height - 1 || first + row == _line ? CellRole.Accent : CellRole.Normal, a.X + a.Width - x);
            var ascii = new string([.. chunk.ToArray().Select(b => b is >= 32 and < 127 ? (char)b : '.')]);
            c.Text(x + 1, a.Y + row, ascii, CellRole.Info, a.X + a.Width - x - 1);
        }
    }
}

/// <summary>"vim": types out real source files from the current folder, with light syntax colouring.</summary>
internal sealed partial class CodePane(DeviousSources sources) : DeviousPane(sources)
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "public", "private", "internal", "protected", "static", "class", "record", "struct", "interface", "void", "return",
        "if", "else", "for", "foreach", "while", "var", "new", "using", "namespace", "async", "await", "function", "const",
        "let", "import", "export", "from", "def", "self", "fn", "func", "package", "param", "true", "false", "null", "try",
        "catch", "throw", "switch", "case", "break", "continue", "in", "is", "not", "and", "or", "elif", "yield", "readonly",
    };

    private DeviousDocument? _document;
    private double _clock;
    private int _typed;
    private int _pause;

    public override string Title => "vim " + (_document?.Name ?? string.Empty);

    public override bool Available => Sources.HasCode;

    public override void Tick(TimeSpan elapsed)
    {
        var steps = Steps(ref _clock, elapsed, 90);
        if (_document is null)
        {
            (_document, _typed) = (Sources.NextCode(), 0);
        }
        else if (!Done)
        {
            _typed += steps;
        }
        else if ((_pause += steps) >= 180)
        {
            // Hold the finished file for two seconds, then start the next one.
            (_document, _typed, _pause) = (Sources.NextCode() ?? _document, 0, 0);
        }
    }

    private bool Done => _document is not { } d || _typed >= d.Lines.Take(200).Sum(l => l.Length + 1);

    public override void Draw(DashboardCanvas c, Area a)
    {
        if (_document is not { } doc)
        {
            return;
        }

        var remaining = _typed;
        var visible = new List<string>();
        foreach (var line in doc.Lines.Take(200))
        {
            if (remaining <= 0)
            {
                break;
            }

            visible.Add(line.Length <= remaining ? line : line[..remaining]);
            remaining -= line.Length + 1;
        }

        var first = Math.Max(0, visible.Count - a.Height);
        for (var row = 0; row < a.Height && first + row < visible.Count; row++)
        {
            var number = (first + row + 1).ToString(CultureInfo.InvariantCulture).PadLeft(4) + " ";
            var x = a.X + c.Text(a.X, a.Y + row, number, CellRole.Muted, a.Width);
            foreach (Match token in Tokens().Matches(visible[first + row]))
            {
                if (x >= a.X + a.Width)
                {
                    break;
                }

                x += c.Text(x, a.Y + row, token.Value, Role(token.Value), a.X + a.Width - x);
            }

            if (first + row == visible.Count - 1 && x < a.X + a.Width)
            {
                c.Text(x, a.Y + row, "█", CellRole.Accent);
            }
        }
    }

    private static CellRole Role(string token) =>
        token.StartsWith("//", StringComparison.Ordinal) || token.StartsWith('#') ? CellRole.Muted
        : token.StartsWith('"') || token.StartsWith('\'') ? CellRole.Warning
        : Keywords.Contains(token) ? CellRole.Title
        : char.IsDigit(token[0]) ? CellRole.Info
        : CellRole.Normal;

    [GeneratedRegex(@"//.*|#.*|""(?:[^""\\]|\\.)*""?|'(?:[^'\\]|\\.)*'?|\w+|\s+|.", RegexOptions.CultureInvariant)]
    private static partial Regex Tokens();
}

/// <summary>"netstat": the machine's real TCP connections and listeners with their processes.</summary>
internal sealed class NetstatPane(DeviousSources sources) : DeviousPane(sources)
{
    private double _clock;
    private int _offset;

    public override string Title => "netstat -ano";

    public override bool Available => Sources.Connections.Count > 0;

    public override void Tick(TimeSpan elapsed) => _offset += Steps(ref _clock, elapsed, 2);

    public override void Draw(DashboardCanvas c, Area a)
    {
        var connections = Sources.Connections;
        if (connections.Count == 0)
        {
            return;
        }

        for (var row = 0; row < Math.Min(a.Height, connections.Count); row++)
        {
            var n = connections[(_offset + row) % connections.Count];
            var remote = n.RemoteAddress is { } r ? $"{r}:{n.RemotePort}" : "*:*";
            var role = n.State switch
            {
                "ESTABLISHED" or "Established" => CellRole.Accent,
                "LISTEN" or "Listen" or "LISTENING" => CellRole.Info,
                _ => CellRole.Muted,
            };
            var line = $"{n.Protocol,-4} {n.LocalAddress}:{n.LocalPort} → {remote}  {n.State}  {n.ProcessName ?? n.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty}";
            c.Text(a.X, a.Y + row, line, role, a.Width);
        }
    }
}

/// <summary>"bmon": real download and upload throughput as bar charts.</summary>
internal sealed class TrafficPane(DeviousSources sources) : DeviousPane(sources)
{
    public override string Title => "bmon";

    public override void Draw(DashboardCanvas c, Area a)
    {
        var half = Math.Max(1, (a.Height - 2) / 2);
        var down = Sources.DownHistory;
        var up = Sources.UpHistory;
        c.Text(a.X, a.Y, "RX " + SystemFormat.Rate(down.Count > 0 ? down[^1] : 0), CellRole.Accent, a.Width);
        Chart(c, a.X, a.Y + 1, a.Width, half, down, CellRole.Accent);
        c.Text(a.X, a.Y + 1 + half, "TX " + SystemFormat.Rate(up.Count > 0 ? up[^1] : 0), CellRole.Info, a.Width);
        Chart(c, a.X, a.Y + 2 + half, a.Width, Math.Max(1, a.Height - half - 2), up, CellRole.Info);
    }

    /// <summary>A multi-row bar chart of the newest values, scaled to <paramref name="max"/> (default: the largest).</summary>
    public static void Chart(DashboardCanvas c, int x, int y, int width, int height, IReadOnlyList<double> values, CellRole role, double? max = null)
    {
        const string Blocks = " ▁▂▃▄▅▆▇█";
        var shown = values.Skip(Math.Max(0, values.Count - width)).ToList();
        var top = Math.Max(max ?? (shown.Count > 0 ? shown.Max() : 0), 1e-9);
        for (var i = 0; i < shown.Count; i++)
        {
            var eighths = (int)Math.Round(shown[i] / top * height * 8);
            for (var row = 0; row < height; row++)
            {
                var fill = Math.Clamp(eighths - ((height - 1 - row) * 8), 0, 8);
                if (fill > 0)
                {
                    c.Text(x + width - shown.Count + i, y + row, Blocks[fill].ToString(), role);
                }
            }
        }
    }
}

/// <summary>"htop": CPU history as a bar chart plus memory and swap meters.</summary>
internal sealed class MetersPane(DeviousSources sources) : DeviousPane(sources)
{
    public override string Title => "htop";

    public override void Draw(DashboardCanvas c, Area a)
    {
        var snapshot = Sources.Processes;
        var cpu = snapshot?.CpuPercent ?? 0;
        c.Text(a.X, a.Y, "CPU [" + SystemFormat.Bar(cpu / 100, Math.Max(4, a.Width - 14)) + "] " + DeviousSources.Percent(cpu), cpu >= 90 ? CellRole.Error : CellRole.Accent, a.Width);
        if (snapshot is { MemoryTotal: > 0 } s)
        {
            var fraction = (double)s.MemoryUsed / s.MemoryTotal;
            c.Text(a.X, a.Y + 1, "Mem [" + SystemFormat.Bar(fraction, Math.Max(4, a.Width - 14)) + "] " + DeviousSources.Percent(fraction * 100), CellRole.Info, a.Width);
        }

        TrafficPane.Chart(c, a.X, a.Y + 3, a.Width, Math.Max(1, a.Height - 3), Sources.CpuHistory, CellRole.Accent, max: 100);
    }
}

/// <summary>"tree": the current folder, scrolling.</summary>
internal sealed class TreePane(DeviousSources sources) : DeviousPane(sources)
{
    private double _clock;
    private int _line;

    public override string Title => "tree " + Sources.Cwd;

    public override bool Available => Sources.Tree.Count > 1;

    public override void Tick(TimeSpan elapsed) => _line += Steps(ref _clock, elapsed, 8);

    public override void Draw(DashboardCanvas c, Area a)
    {
        var tree = Sources.Tree;
        if (tree.Count == 0)
        {
            return;
        }

        var end = _line % (tree.Count + a.Height);
        var first = Math.Max(0, end - a.Height);
        for (var row = 0; row < a.Height && first + row < Math.Min(end, tree.Count); row++)
        {
            var line = tree[first + row];
            c.Text(a.X, a.Y + row, line, line.EndsWith('/') ? CellRole.Info : CellRole.Normal, a.Width);
        }
    }
}

/// <summary>"sha256sum": real hashes of the files in the current folder as they are computed.</summary>
internal sealed class HashPane(DeviousSources sources) : DeviousPane(sources)
{
    private readonly List<string> _lines = [];
    private double _clock;

    public override string Title => "sha256sum";

    public override void Tick(TimeSpan elapsed)
    {
        for (var i = Steps(ref _clock, elapsed, 6); i > 0 && Sources.TryTakeHash(out var line); i--)
        {
            _lines.Add(line);
            if (_lines.Count > 200)
            {
                _lines.RemoveAt(0);
            }
        }
    }

    public override void Draw(DashboardCanvas c, Area a)
    {
        var first = Math.Max(0, _lines.Count - a.Height);
        for (var row = 0; row < a.Height && first + row < _lines.Count; row++)
        {
            var line = _lines[first + row];
            var x = a.X + c.Text(a.X, a.Y + row, line[..Math.Min(64, line.Length)], first + row == _lines.Count - 1 ? CellRole.Accent : CellRole.Muted, a.Width);
            c.Text(x, a.Y + row, line[Math.Min(64, line.Length)..], CellRole.Normal, a.X + a.Width - x);
        }
    }
}

/// <summary>"tail -f": the end of a real log (Pickle's own, or the system's), line by line.</summary>
internal sealed class LogPane(DeviousSources sources) : DeviousPane(sources)
{
    private DeviousDocument? _log;
    private double _clock;
    private int _shown;

    public override string Title => "tail -f " + (_log?.Name ?? string.Empty);

    public override bool Available => Sources.HasLogs;

    public override void Tick(TimeSpan elapsed)
    {
        _shown += Steps(ref _clock, elapsed, 4);
        if (_log is null || _shown > _log.Lines.Count + 20)
        {
            _log = Sources.NextLog() ?? _log;
            _shown = 0;
        }
    }

    public override void Draw(DashboardCanvas c, Area a)
    {
        if (_log is not { } log)
        {
            return;
        }

        var end = Math.Min(_shown, log.Lines.Count);
        var first = Math.Max(0, end - a.Height);
        for (var row = 0; row < a.Height && first + row < end; row++)
        {
            var line = log.Lines[first + row];
            var role = line.Contains("error", StringComparison.OrdinalIgnoreCase) || line.Contains("fail", StringComparison.OrdinalIgnoreCase) ? CellRole.Error
                : line.Contains("warn", StringComparison.OrdinalIgnoreCase) ? CellRole.Warning
                : CellRole.Normal;
            c.Text(a.X, a.Y + row, line, role, a.Width);
        }
    }
}

/// <summary>"git log --graph": the current repository's real history.</summary>
internal sealed class GitPane(DeviousSources sources) : DeviousPane(sources)
{
    private double _clock;
    private int _offset;

    public override string Title => "git log --graph " + (Sources.RepositoryName ?? string.Empty);

    public override bool Available => Sources.Commits.Count > 0;

    public override void Tick(TimeSpan elapsed) => _offset += Steps(ref _clock, elapsed, 1.2);

    public override void Draw(DashboardCanvas c, Area a)
    {
        var commits = Sources.Commits;
        if (commits.Count == 0)
        {
            return;
        }

        for (var row = 0; row < Math.Min(a.Height, commits.Count); row++)
        {
            var commit = commits[(_offset + row) % commits.Count];
            var x = a.X + c.Text(a.X, a.Y + row, (commit.Graph.Length > 0 ? commit.Graph : "*") + " ", CellRole.Warning, a.Width);
            x += c.Text(x, a.Y + row, commit.ShortSha + " ", CellRole.Accent, a.X + a.Width - x);
            if (commit.Refs.Count > 0)
            {
                x += c.Text(x, a.Y + row, "(" + string.Join(", ", commit.Refs) + ") ", CellRole.Info, a.X + a.Width - x);
            }

            x += c.Text(x, a.Y + row, commit.Subject + " ", CellRole.Normal, a.X + a.Width - x);
            c.Text(x, a.Y + row, "— " + commit.Author, CellRole.Muted, a.X + a.Width - x);
        }
    }
}

/// <summary>"neofetch": the pickle and the machine's real facts.</summary>
internal sealed class SystemPane(DeviousSources sources) : DeviousPane(sources)
{
    private static readonly string[] Art =
    [
        "       ▄▄▄ ",
        "     ▄█▓▒▓█",
        "   ▄█▒▓▒▓█▀",
        "  █▓▒▓▒▓█▀ ",
        " █▒▓▒▓█▀   ",
        "  ▀▀▀▀     ",
    ];

    public override string Title => "neofetch";

    public override void Draw(DashboardCanvas c, Area a)
    {
        var textX = a.X;
        if (a.Width >= 40)
        {
            for (var i = 0; i < Art.Length && i < a.Height; i++)
            {
                c.Text(a.X, a.Y + i, Art[i], CellRole.Accent);
            }

            textX = a.X + Art[0].Length + 2;
        }

        if (Sources.System is not { } s)
        {
            return;
        }

        var lines = new List<(string Label, string Value)>
        {
            ($"{s.UserName}@{s.MachineName}", string.Empty),
            ("OS", s.OperatingSystem),
            ("CPU", (s.CpuModel ?? "?") + $" ({s.LogicalProcessors})"),
            ("Uptime", DashboardLayout.Uptime(s.Uptime)),
            ("Memory", Sources.Processes is { MemoryTotal: > 0 } p ? $"{SystemFormat.Bytes(p.MemoryUsed)} / {SystemFormat.Bytes(p.MemoryTotal)}" : "?"),
            ("Shell", "Pickle (PowerShell 7)"),
        };
        var width = a.X + a.Width - textX;
        for (var i = 0; i < lines.Count && i < a.Height; i++)
        {
            var (label, value) = lines[i];
            var x = textX + c.Text(textX, a.Y + i, label, CellRole.Title, width);
            if (value.Length > 0)
            {
                c.Text(x, a.Y + i, ": " + value, CellRole.Normal, textX + width - x);
            }
        }
    }
}

/// <summary>"cmatrix": rain made of the machine's real process names, addresses and hashes.</summary>
internal sealed class MatrixPane(DeviousSources sources, int seed) : DeviousPane(sources)
{
    private readonly Random _random = new(seed);
    private readonly List<(double Y, double Speed, string Text)> _drops = [];
    private int _width;
    private int _height;

    public override string Title => "cmatrix";

    public override void Tick(TimeSpan elapsed)
    {
        for (var i = 0; i < _drops.Count; i++)
        {
            var (y, speed, text) = _drops[i];
            y += speed * elapsed.TotalSeconds;
            _drops[i] = y - text.Length > _height ? New() : (y, speed, text);
        }
    }

    public override void Draw(DashboardCanvas c, Area a)
    {
        if (a.Width != _width || a.Height != _height)
        {
            (_width, _height) = (a.Width, a.Height);
            _drops.Clear();
            for (var i = 0; i < a.Width; i++)
            {
                var drop = New();
                _drops.Add((drop.Y - (_random.NextDouble() * a.Height), drop.Speed, drop.Text));
            }
        }

        for (var column = 0; column < _drops.Count && column < a.Width; column += 2)
        {
            var (y, _, text) = _drops[column];
            var head = (int)y;
            for (var k = 0; k < text.Length; k++)
            {
                var row = head - k;
                if (row >= 0 && row < a.Height)
                {
                    c.Text(a.X + column, a.Y + row, text[k].ToString(), k == 0 ? CellRole.Title : k < 4 ? CellRole.Accent : CellRole.Muted);
                }
            }
        }
    }

    private (double Y, double Speed, string Text) New()
    {
        var words = new List<string>();
        if (Sources.Processes is { } p && p.Processes.Count > 0)
        {
            words.Add(p.Processes[_random.Next(p.Processes.Count)].Name);
        }

        if (Sources.Connections is { Count: > 0 } connections)
        {
            words.Add(connections[_random.Next(connections.Count)].RemoteAddress ?? "0.0.0.0");
        }

        words.Add(_random.Next().ToString("x8", CultureInfo.InvariantCulture));
        var text = string.Concat(words[_random.Next(words.Count)].Where(ch => !char.IsWhiteSpace(ch) && ch < 0x2000));
        return (0, 4 + (_random.NextDouble() * 10), text.Length == 0 ? "0" : text);
    }
}
