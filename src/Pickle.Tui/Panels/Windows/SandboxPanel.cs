using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Tui.Widgets;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Pickle.Tui.Panels.Windows;

/// <summary>
/// Windows Sandbox (Alt+X): presets and saved setups on the left, every .wsb option plus Pickle's customisations on
/// the right. F5 launches, F2 saves, F3 exports a .wsb, F4 previews what will be written, F8 deletes, F9 turns the
/// Windows feature on.
/// </summary>
public sealed class SandboxPanel : WindowsPanelBase
{
    private static readonly string[] SwitchLabels = ["Default", "On", "Off"];

    private readonly ListView _list = new() { X = 0, Y = 1, Width = 26, Height = Dim.Fill() };
    private readonly Label _status = new() { X = 0, Y = 0, Width = Dim.Fill(), Height = 1 };
    private readonly View _form = new() { Y = 1, Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true, BorderStyle = LineStyle.Single, Title = "Configuration" };
    private readonly ObservableCollection<string> _labels = [];
    private readonly List<(SandboxConfig Config, bool Saved)> _entries = [];
    private int _y;
    private bool _loading;

    public SandboxPanel(PanelContext context)
        : base(context, "Windows Sandbox")
    {
        _list.SetSource(_labels);
        _form.X = Pos.Right(_list) + 1;
        _form.ViewportSettings |= ViewportSettingsFlags.HasVerticalScrollBar;
        _list.ValueChanged += (_, e) =>
        {
            if (!_loading && e.NewValue is { } index && index >= 0 && index < _entries.Count)
            {
                Load(_entries[index].Config);
            }
        };
        _list.KeyDown += (_, key) =>
        {
            if (key == Key.CursorRight && _form.SubViews.FirstOrDefault(v => v.CanFocus) is { } first)
            {
                first.SetFocus();
                key.Handled = true;
            }
        };
        Body.Add(_status, _list, _form);

        AddHint(Key.F5, "Launch", Launch);
        AddHint(Key.F2, "Save", Save);
        AddHint(Key.F3, "Export .wsb", Export);
        AddHint(Key.F4, "Preview", Preview);
        AddHint(Key.F8, "Delete", Delete);
        AddHint(Key.F9, "Turn on", EnableFeature);

        Working = new SandboxConfig();
        Reload(Context.Argument);
    }

    /// <summary>The configuration being edited (a copy; presets and saved files change only on Save).</summary>
    public SandboxConfig Working { get; private set; }

    internal IReadOnlyList<string> Entries => _labels;

    internal string StatusText => _status.Text ?? string.Empty;

    internal string? LastMessage { get; private set; }

    private ISandboxService? Sandbox => Service<ISandboxService>();

    protected override void Opened() => ShowStatus();

    internal void Select(string name)
    {
        var index = _entries.FindIndex(e => e.Config.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _list.SelectedItem = index;
            Load(_entries[index].Config);
        }
    }

    internal void Launch()
    {
        if (Sandbox is not { } sandbox)
        {
            return;
        }

        var config = Clone(Working);
        Load<SandboxOperationResult>(ct => sandbox.LaunchAsync(config, ct), Report, "starting the sandbox…");
    }

    internal void Save()
    {
        if (Sandbox is not { } sandbox)
        {
            return;
        }

        if (Working.Name is not { Length: > 0 } || _entries.Any(e => !e.Saved && e.Config.Name.Equals(Working.Name, StringComparison.OrdinalIgnoreCase)))
        {
            var name = AskText("Save sandbox", "Name for this setup:", Working.Name + " (mine)");
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            Working.Name = name.Trim();
        }

        try
        {
            sandbox.Save(Clone(Working));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Fail(ex.Message);
            return;
        }

        var saved = Working.Name;
        Reload(saved);
        LastMessage = $"Saved '{saved}'.";
        _status.Text = LastMessage;
    }

    internal void Export()
    {
        if (Sandbox is not { } sandbox)
        {
            return;
        }

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var initial = Path.Combine(desktop.Length > 0 ? desktop : Pickle.Shell.CurrentDirectory, Working.Name + ".wsb");
        var path = AskText("Export .wsb", "Write the sandbox file to:", initial);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        Report(sandbox.Export(Clone(Working), path.Trim()));
    }

    /// <summary>The .wsb (and setup script) the current configuration produces.</summary>
    internal string PreviewText()
    {
        if (Sandbox is not { } sandbox)
        {
            return string.Empty;
        }

        var errors = sandbox.Validate(Working);
        var text = sandbox.BuildWsb(Working, "<setup folder>");
        var script = sandbox.BuildSetupScript(Working) is { } setup ? "\n\n── setup.ps1 (runs at logon) ──\n" + setup : string.Empty;
        return (errors.Count > 0 ? "Problems:\n  " + string.Join("\n  ", errors) + "\n\n" : string.Empty) + text + script;
    }

    internal void Preview()
    {
        if (App is not { } app)
        {
            return;
        }

        using var dialog = new Dialog { Title = "Preview: " + Working.Name, Width = Dim.Percent(85), Height = Dim.Percent(85) };
        var pane = MakeText(".wsb");
        pane.Height = Dim.Fill(1);
        pane.Content = PreviewText();
        dialog.Add(pane);
        dialog.AddButton(new Button { Title = "_Close" });
        dialog.SetScheme(Schemes.Dialog);
        app.Run(dialog);
    }

    internal void Delete()
    {
        var index = _list.SelectedItem ?? -1;
        if (Sandbox is not { } sandbox || index < 0 || index >= _entries.Count || !_entries[index].Saved)
        {
            Fail("Only saved setups can be deleted (presets are built in).");
            return;
        }

        var name = _entries[index].Config.Name;
        if (!Ask("Delete", $"Delete the saved sandbox '{name}'?"))
        {
            return;
        }

        sandbox.Delete(name);
        Reload(null);
    }

    internal void EnableFeature()
    {
        if (Sandbox is not { } sandbox)
        {
            return;
        }

        if (!Ask("Turn on Windows Sandbox", "Turn on the Windows Sandbox feature? This needs administrator rights and usually a restart."))
        {
            return;
        }

        Load<SandboxOperationResult>(
            ct => sandbox.EnableFeatureAsync(null, ct),
            result =>
            {
                Report(result);
                ShowStatus();
            },
            "turning on Windows Sandbox…");
    }

    private void Report(SandboxOperationResult result)
    {
        LastMessage = result.Message;
        if (result.Success)
        {
            _status.Text = "✓ " + result.Message;
        }
        else
        {
            Fail(result.Message);
        }
    }

    private void ShowStatus()
    {
        if (Sandbox is not { } sandbox)
        {
            _status.Text = "Windows Sandbox is not available.";
            return;
        }

        var status = sandbox.GetStatus();
        _status.Text = !status.Supported ? "✖ " + (status.Message ?? "Windows Sandbox isn't supported here.")
            : !status.FeatureEnabled ? "⚠ Windows Sandbox is off — F9 turns it on (administrator, then restart)."
            : status.Running ? "● A sandbox is running (Windows runs one at a time)."
            : "✓ Windows Sandbox is on. F5 launches the selected setup.";
    }

    private void Reload(string? select)
    {
        _loading = true;
        try
        {
            _entries.Clear();
            _labels.Clear();
            if (Sandbox is { } sandbox)
            {
                foreach (var preset in sandbox.Presets)
                {
                    _entries.Add((preset, false));
                    _labels.Add("◆ " + preset.Name);
                }

                foreach (var saved in sandbox.LoadSaved())
                {
                    _entries.Add((saved, true));
                    _labels.Add("● " + saved.Name);
                }
            }

            var index = select is null ? 0 : Math.Max(0, _entries.FindIndex(e => e.Config.Name.Equals(select, StringComparison.OrdinalIgnoreCase)));
            if (_entries.Count > 0)
            {
                _list.SelectedItem = index;
                Load(_entries[index].Config);
            }
            else
            {
                Load(new SandboxConfig());
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private static SandboxConfig Clone(SandboxConfig config) =>
        JsonSerializer.Deserialize<SandboxConfig>(JsonSerializer.Serialize(config, PickleJson.Options), PickleJson.Options) ?? new SandboxConfig();

    private void Load(SandboxConfig config)
    {
        Working = Clone(config);
        foreach (var view in _form.SubViews.ToList())
        {
            _form.Remove(view);
            view.Dispose();
        }

        _y = 0;
        var c = Working;
        TextRow("Name", c.Name, v => c.Name = v);
        TextRow("Description", c.Description, v => c.Description = v);

        Header("Isolation");
        SwitchRow("Networking", c.Networking, v => c.Networking = v);
        SwitchRow("GPU (vGPU)", c.VGpu, v => c.VGpu = v);
        SwitchRow("Clipboard", c.ClipboardRedirection, v => c.ClipboardRedirection = v);
        SwitchRow("Printers", c.PrinterRedirection, v => c.PrinterRedirection = v);
        SwitchRow("Microphone", c.AudioInput, v => c.AudioInput = v);
        SwitchRow("Camera", c.VideoInput, v => c.VideoInput = v);
        SwitchRow("Protected client", c.ProtectedClient, v => c.ProtectedClient = v);
        TextRow("Memory (MB)", c.MemoryInMB?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, v => c.MemoryInMB = int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mb) ? mb : null);

        Header("Shared folders (one per line)");
        LinesRow("Read-only", 2, Folders(c, readOnly: true), v => SetFolders(c, v, readOnly: true));
        LinesRow("Read-write", 2, Folders(c, readOnly: false), v => SetFolders(c, v, readOnly: false));
        CheckRow("Open the first shared folder at logon", c.OpenMappedFolder, v => c.OpenMappedFolder = v);

        Header("Customise");
        CheckRow("Dark mode", c.DarkMode, v => c.DarkMode = v);
        CheckRow("Show file extensions", c.ShowFileExtensions, v => c.ShowFileExtensions = v);
        CheckRow("Show hidden files", c.ShowHiddenFiles, v => c.ShowHiddenFiles = v);
        CheckRow("Install winget", c.InstallWinget, v => c.InstallWinget = v);
        TextRow("winget packages", string.Join(", ", c.WingetPackages), v => c.WingetPackages = [.. v.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)]);
        CheckRow("Share this Pickle (on PATH)", c.IncludePickle, v => c.IncludePickle = v);
        CheckRow("Start Pickle at logon", c.StartPickle, v => c.StartPickle = v);
        TextRow("Start page (Edge)", c.StartUrl ?? string.Empty, v => c.StartUrl = v.Length == 0 ? null : v);
        TextRow("Logon command", c.LogonCommand ?? string.Empty, v => c.LogonCommand = v.Length == 0 ? null : v);
        LinesRow("Setup script (PowerShell)", 4, c.SetupScript ?? string.Empty, v => c.SetupScript = v.Trim().Length == 0 ? null : v);

        if (c.Description.Length > 0)
        {
            _form.Title = "Configuration — " + c.Description;
        }

        _form.SetContentHeight(_y);
        _form.SetNeedsDraw();
    }

    private static string Folders(SandboxConfig c, bool readOnly) =>
        string.Join("\n", c.MappedFolders.Where(f => f.ReadOnly == readOnly).Select(f => f.SandboxFolder is { Length: > 0 } inside ? $"{f.HostFolder} => {inside}" : f.HostFolder));

    private static void SetFolders(SandboxConfig c, string text, bool readOnly)
    {
        c.MappedFolders.RemoveAll(f => f.ReadOnly == readOnly);
        foreach (var line in text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split("=>", 2, StringSplitOptions.TrimEntries);
            c.MappedFolders.Add(new SandboxMappedFolder { HostFolder = parts[0].Trim('"'), SandboxFolder = parts.Length > 1 && parts[1].Length > 0 ? parts[1].Trim('"') : null, ReadOnly = readOnly });
        }
    }

    private const int LabelWidth = 22;

    /// <summary>Adds a form control and scrolls the form to it when it gets focus (Tab moves past the visible rows).</summary>
    private void AddField(View view)
    {
        view.HasFocusChanged += (_, e) =>
        {
            if (!e.NewValue)
            {
                return;
            }

            var viewport = _form.Viewport;
            var top = view.Frame.Y - 1;
            var bottom = view.Frame.Y + view.Frame.Height;
            if (top < viewport.Y)
            {
                _form.Viewport = viewport with { Y = Math.Max(0, top) };
            }
            else if (viewport.Height > 0 && bottom > viewport.Y + viewport.Height)
            {
                _form.Viewport = viewport with { Y = bottom - viewport.Height };
            }
        };
        _form.Add(view);
    }

    private void Header(string text)
    {
        _y++;
        _form.Add(new Label { Text = "── " + text + " ──", X = 0, Y = _y, Width = Dim.Fill() });
        _y++;
    }

    private void TextRow(string label, string value, Action<string> set)
    {
        _form.Add(new Label { Text = label, X = 0, Y = _y + 1, Width = LabelWidth });
        var field = InputBox.Boxed(new TextField { X = LabelWidth, Y = _y, Width = Dim.Fill(1), Text = value });
        field.TextChanged += (_, _) => set((field.Text ?? string.Empty).Trim());
        AddField(field);
        _y += 1 + InputBox.Chrome;
    }

    private void SwitchRow(string label, SandboxSwitch value, Action<SandboxSwitch> set)
    {
        _form.Add(new Label { Text = label, X = 0, Y = _y, Width = LabelWidth });
        var selector = new OptionSelector { X = LabelWidth + 1, Y = _y, Labels = SwitchLabels, Orientation = Orientation.Horizontal, Value = (int)value };
        selector.ValueChanged += (_, _) => set((SandboxSwitch)Math.Clamp(selector.Value ?? 0, 0, 2));
        AddField(selector);
        _y++;
    }

    private void CheckRow(string label, bool value, Action<bool> set)
    {
        var box = new CheckBox { X = LabelWidth + 1, Y = _y, Text = label, Value = value ? CheckState.Checked : CheckState.UnChecked };
        box.ValueChanged += (_, e) => set(e.NewValue == CheckState.Checked);
        AddField(box);
        _y++;
    }

    // Terminal.Gui 2.5 marks TextView obsolete in favour of a separate editor package; it is still the only
    // multi-line input that ships with it.
#pragma warning disable CS0618
    private void LinesRow(string label, int height, string value, Action<string> set)
    {
        _form.Add(new Label { Text = label, X = 0, Y = _y + 1, Width = LabelWidth });
        var text = InputBox.Boxed(new TextView { X = LabelWidth, Y = _y, Width = Dim.Fill(1), Text = value, TabKeyAddsTab = false }, height);
        text.ContentsChanged += (_, _) => set(text.Text ?? string.Empty);
        AddField(text);
        _y += height + InputBox.Chrome;
    }
#pragma warning restore CS0618
}
