using System.Collections.ObjectModel;
using System.Diagnostics;
using Pickle.Abstractions;
using Pickle.Tui.Widgets;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Pickle.Tui.Panels.NetTools;

/// <summary>
/// Network tools (Alt+T, <c>pk tools</c>): port scan, host discovery, DNS, traceroute, whois, TLS certificates, HTTP,
/// subnets, Wake-on-LAN and local addresses, without installing anything. F5 runs, F6 stops, F8 puts the equivalent
/// <c>pk</c> command on the prompt. The argument picks a tool (and, after a space, its first field).
/// </summary>
public sealed class NetToolsPanel : PanelWindow
{
    public const string PanelId = "nettools";
    private const int MaxLines = 5000;

    private readonly IReadOnlyList<NetTool> _tools;
    private readonly ListView _toolList = new() { X = 0, Y = 0, Width = 20, Height = Dim.Fill() };
    private readonly Label _description = new() { Y = 0, Width = Dim.Fill(), Height = 2 };
    private readonly View _form = new() { Y = 2, Width = Dim.Fill(), CanFocus = true };
    private readonly Label _status = new() { Width = Dim.Fill(), Height = 1 };
    private readonly ListView _results = new() { Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
    private readonly ObservableCollection<string> _lines = [];
    private readonly Dictionary<string, View> _editors = [];
    private CancellationTokenSource? _running;
    private NetTool _tool;

    public NetToolsPanel(PanelContext context)
        : base(context, "Network tools")
    {
        _tools = NetToolCatalog.Create(NetToolCatalog.GuessLocalNetwork());
        _toolList.SetSource(new ObservableCollection<string>(_tools.Select(t => t.Title)));
        _results.SetSource(_lines);

        var right = new View { X = Pos.Right(_toolList) + 1, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        _description.X = 0;
        _form.X = 0;
        var output = new FrameView { X = 0, Y = Pos.Bottom(_form), Width = Dim.Fill(), Height = Dim.Fill(), Title = "Results" };
        _status.X = 0;
        _status.Y = Pos.AnchorEnd(1);
        _results.X = 0;
        _results.Y = 0;
        _results.Height = Dim.Fill(1);
        output.Add(_results, _status);
        right.Add(_description, _form, output);
        Body.Add(_toolList, right);

        _toolList.ValueChanged += (_, e) =>
        {
            if (e.NewValue is { } index && index >= 0 && index < _tools.Count && _tools[index] != _tool)
            {
                ShowTool(_tools[index]);
            }
        };
        _toolList.KeyDown += (_, key) =>
        {
            if (key == Key.Enter || key == Key.CursorRight)
            {
                _form.SubViews.FirstOrDefault(v => v.CanFocus)?.SetFocus();
                key.Handled = true;
            }
        };

        AddHint(Key.F5, "Run", Run);
        AddHint(Key.F6, "Stop", Stop);
        AddHint(Key.F8, "Insert pk command", InsertCommand);

        var (tool, first) = ResolveArgument(context.Argument);
        _tool = tool;
        _toolList.SelectedItem = _tools.ToList().IndexOf(tool);
        ShowTool(tool);
        if (first is not null && _editors.GetValueOrDefault(tool.Fields[0].Key) is TextField field)
        {
            field.Text = first;
        }
    }

    public string ToolId => _tool.Id;

    internal IReadOnlyList<string> Lines => _lines;

    internal string StatusText => _status.Text ?? string.Empty;

    internal bool IsBusy => _running is not null;

    internal void SetField(string key, string value)
    {
        switch (_editors.GetValueOrDefault(key))
        {
            case TextField text:
                text.Text = value;
                break;
            case CheckBox check:
                check.Value = value == "true" ? CheckState.Checked : CheckState.UnChecked;
                break;
            default:
                throw new ArgumentException($"{_tool.Id} has no field '{key}'.");
        }
    }

    internal IReadOnlyDictionary<string, string> Values() =>
        _tool.Fields.ToDictionary(
            f => f.Key,
            f => _editors.GetValueOrDefault(f.Key) switch
            {
                CheckBox check => check.Value == CheckState.Checked ? "true" : string.Empty,
                View view => (view.Text ?? string.Empty).Trim(),
                _ => f.Default,
            });

    /// <summary>The equivalent <c>pk</c> command for the current values.</summary>
    internal string CommandLine => _tool.Command(Values());

    internal void Run()
    {
        if (_running is not null)
        {
            return;
        }

        var tool = _tool;
        var values = Values();
        _lines.Clear();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(Lifetime);
        _running = cts;
        var watch = Stopwatch.StartNew();
        _status.Text = "running… (F6 stops)";
        _ = Task.Run(async () =>
        {
            string done;
            try
            {
                await tool.Run(values, Write, text => OnUi(() =>
                {
                    if (ReferenceEquals(_running, cts))
                    {
                        _status.Text = text + $"  · {watch.Elapsed.TotalSeconds:0} s";
                    }
                }), cts.Token).ConfigureAwait(false);
                done = $"done in {watch.Elapsed.TotalSeconds:0.0} s";
            }
            catch (OperationCanceledException)
            {
                done = "stopped";
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Write("✖ " + ex.Message);
                done = "failed";
            }

            OnUi(() =>
            {
                _status.Text = done;
                _running = null;
                cts.Dispose();
            });
        });
    }

    internal void Stop() => _running?.Cancel();

    internal void InsertCommand() => Complete(new PanelResult(PanelResultKind.ReplaceInput, CommandLine));

    private void Write(string line) => OnUi(() =>
    {
        _lines.Add(line);
        if (_lines.Count > MaxLines)
        {
            _lines.RemoveAt(0);
        }

        if (!_results.HasFocus)
        {
            _results.SelectedItem = _lines.Count - 1;
        }
    });

    private (NetTool Tool, string? First) ResolveArgument(string? argument)
    {
        var parts = (argument ?? string.Empty).Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var tool = parts.Length > 0 ? _tools.FirstOrDefault(t => t.Id.Equals(parts[0], StringComparison.OrdinalIgnoreCase)) : null;
        return tool is null ? (_tools[0], null) : (tool, parts.Length > 1 ? parts[1] : null);
    }

    private void ShowTool(NetTool tool)
    {
        Stop();
        _tool = tool;
        _description.Text = tool.Description;
        foreach (var view in _form.SubViews.ToList())
        {
            _form.Remove(view);
            view.Dispose();
        }

        _editors.Clear();
        var y = 0;
        foreach (var field in tool.Fields)
        {
            if (field.Kind == NetFieldKind.Flag)
            {
                var check = new CheckBox { X = 20, Y = y, Text = field.Label, Value = field.Default == "true" ? CheckState.Checked : CheckState.UnChecked };
                _form.Add(check);
                _editors[field.Key] = check;
                y++;
                continue;
            }

            _form.Add(new Label { Text = field.Label, X = 0, Y = y + 1, Width = 19 });
            var text = InputBox.Boxed(new TextField { X = 20, Y = y, Width = Dim.Fill(1), Text = field.Default });
            text.KeyDown += (_, key) =>
            {
                if (key == Key.Enter)
                {
                    Run();
                    key.Handled = true;
                }
            };
            _form.Add(text);
            _editors[field.Key] = text;
            y += 1 + InputBox.Chrome;
        }

        _form.Height = y;
        _lines.Clear();
        _status.Text = "F5 or Enter runs · F8 puts the pk command on the prompt";
        SetNeedsLayout();
    }
}
