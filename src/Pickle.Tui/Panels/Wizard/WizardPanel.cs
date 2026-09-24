using System.Collections.ObjectModel;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Tui.Widgets;
using Pickle.Wizards;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Pickle.Tui.Panels.Wizard;

/// <summary>
/// Form for one wizard (Argument = wizard id or command name). When opened from the editor the typed command is parsed
/// back into the form and Run/Insert replace just that command within the input line. No wizard → a picker.
/// </summary>
internal sealed class WizardPanel : PanelWindow
{
    private const int LabelWidth = 28;
    private const string NoPreset = "(choose a preset)";

    private readonly Func<string, bool> _toolExists;
    private readonly Dictionary<string, Dictionary<string, string>> _stashedModeValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, View> _editors = new(StringComparer.Ordinal);
    private readonly List<FormItem> _formItems = [];
    private Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private IReadOnlyList<WizardDefinition> _pickerMatches = [];
    private WizardDefinition? _definition;
    private string? _modeId;
    private string _input = string.Empty;
    private int _replaceStart;
    private int _replaceLength;
    private bool _loading;

    private View? _content;
    private View? _form;
    private TextField? _extras;
    private Label? _help;
    private Label? _messages;
    private Label? _preview;
    private Button? _install;
    private DropDownList? _modeList;
    private ListView? _pickerList;
    private Label? _descriptionLabel;

    public WizardPanel(PanelContext context, Func<string, bool>? toolExists = null)
        : base(context, "Command wizard")
    {
        _toolExists = toolExists ?? IsOnPath;
        AddHint(Key.F5, "Run", RunCommand);
        AddHint(Key.F6, "Insert", InsertCommand);
        AddHint(Key.F7, "Copy", CopyCommand);
        AddHint(Key.F8, "Save as alias", SaveAliasInteractive);

        if (Resolve(context) is { } definition)
        {
            Load(definition, context.CurrentInput);
        }
        else
        {
            ShowPicker();
        }
    }

    internal WizardDefinition? Definition => _definition;

    internal string? ModeId => _modeId;

    internal IReadOnlyDictionary<string, string> Values => _values;

    internal WizardCommand? Command { get; private set; }

    internal string PreviewText => Command?.CommandLine ?? string.Empty;

    internal string MessagesText => _messages?.Text ?? string.Empty;

    internal bool InstallOffered => _install?.Visible == true;

    internal IReadOnlyList<WizardDefinition> PickerMatches => _pickerMatches;

    internal TextField? ExtraArgumentsField => _extras;

    /// <summary>Editor for a value key (option id, or "optionId.sub" for template fields).</summary>
    internal View? Editor(string key) => _editors.GetValueOrDefault(key);

    /// <summary>Test hook replacing the alias-name dialog.</summary>
    internal Func<string?>? AskAliasName { get; set; }

    /// <summary>The whole input line with the wizard's command in place of the one that was typed.</summary>
    internal string ResultLine
    {
        get
        {
            var line = Command?.CommandLine ?? string.Empty;
            return _replaceLength > 0
                ? _input[.._replaceStart] + line + _input[(_replaceStart + _replaceLength)..]
                : line;
        }
    }

    internal static WizardDefinition? Resolve(PanelContext context)
    {
        var wizards = context.Pickle.Wizards;
        if (!string.IsNullOrWhiteSpace(context.Argument))
        {
            return wizards.Get(context.Argument) ?? wizards.FindForCommand(context.Argument);
        }

        return string.IsNullOrWhiteSpace(context.CurrentInput) ? null : FindInInput(wizards, context.CurrentInput);
    }

    internal static WizardDefinition? FindInInput(IWizardRegistry wizards, string input) =>
        CommandTokenizer.ParseCommands(input).Select(c => wizards.FindForCommand(c.Name)).FirstOrDefault(w => w is not null);

    /// <summary>Ranks wizards for the picker: prefix, then substring, then subsequence matches of id/title/description.</summary>
    internal static IReadOnlyList<WizardDefinition> Filter(IEnumerable<WizardDefinition> wizards, string query)
    {
        var q = query.Trim();
        if (q.Length == 0)
        {
            return [.. wizards.OrderBy(w => w.Title, StringComparer.OrdinalIgnoreCase)];
        }

        static bool Subsequence(string text, string pattern)
        {
            var i = 0;
            foreach (var c in text)
            {
                if (i < pattern.Length && char.ToLowerInvariant(c) == char.ToLowerInvariant(pattern[i]))
                {
                    i++;
                }
            }

            return i == pattern.Length;
        }

        int Rank(WizardDefinition w)
        {
            if (w.Id.StartsWith(q, StringComparison.OrdinalIgnoreCase) || w.Title.StartsWith(q, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            if (w.Id.Contains(q, StringComparison.OrdinalIgnoreCase) || w.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                || w.Aliases.Any(a => a.Contains(q, StringComparison.OrdinalIgnoreCase)))
            {
                return 1;
            }

            if (w.Description.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }

            return Subsequence(w.Id, q) || Subsequence(w.Title, q) ? 3 : -1;
        }

        return
        [
            .. wizards.Select(w => (Wizard: w, Rank: Rank(w)))
                .Where(x => x.Rank >= 0)
                .OrderBy(x => x.Rank)
                .ThenBy(x => x.Wizard.Title, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Wizard),
        ];
    }

    internal void Pick(WizardDefinition definition) => Load(definition, null);

    internal void SelectMode(string modeId)
    {
        if (_definition is null || string.Equals(modeId, _modeId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var globals = GlobalKeys(_definition);
        if (_modeId is not null)
        {
            _stashedModeValues[_modeId] = _values.Where(kv => !globals.Contains(Root(kv.Key))).ToDictionary(StringComparer.Ordinal);
        }

        var next = new Dictionary<string, string>(_values.Where(kv => globals.Contains(Root(kv.Key))), StringComparer.Ordinal);
        foreach (var (key, value) in _stashedModeValues.GetValueOrDefault(modeId) ?? [])
        {
            next[key] = value;
        }

        _modeId = WizardSchema.ResolveMode(_definition, modeId)?.Id;
        _values = next;
        RebuildForm();
    }

    internal void ApplyPreset(WizardPreset preset)
    {
        if (_definition is null)
        {
            return;
        }

        _modeId = WizardSchema.ResolveMode(_definition, preset.Mode)?.Id;
        _values = new Dictionary<string, string>(preset.Values, StringComparer.Ordinal);
        _stashedModeValues.Clear();
        if (_extras is not null)
        {
            _extras.Text = string.Empty;
        }

        RebuildForm();
    }

    /// <summary>Set a value as if typed into its field (updates the editor, then the preview).</summary>
    internal void SetFieldValue(string key, string? value)
    {
        switch (Editor(key))
        {
            case CheckBox box:
                box.Value = WizardEngine.IsTrue(value) ? CheckState.Checked : CheckState.UnChecked;
                break;
            case View editor:
                editor.Text = value ?? string.Empty;
                break;
        }

        SetValue(key, value);
    }

    internal void RunCommand()
    {
        if (Command is null)
        {
            return;
        }

        if (!Command.IsValid)
        {
            ShowError(string.Join("\n", Command.Errors));
            return;
        }

        Complete(new PanelResult(PanelResultKind.RunCommand, ResultLine));
    }

    internal void InsertCommand()
    {
        if (Command is not null)
        {
            Complete(new PanelResult(PanelResultKind.ReplaceInput, ResultLine));
        }
    }

    internal void CopyCommand()
    {
        if (Command is not null && App?.Clipboard is { } clipboard && _messages is not null)
        {
            _messages.Text = clipboard.TrySetClipboardData(Command.CommandLine) ? "Copied to the clipboard." : "The clipboard is not available.";
        }
    }

    internal bool SaveAlias(string name)
    {
        if (Command is null || _definition is null || !AliasNameIsValid(name))
        {
            return false;
        }

        Pickle.Aliases.Set(new AliasDefinition
        {
            Name = name.Trim(),
            Kind = AliasKind.Simple,
            Body = Command.CommandLine,
            Description = $"{_definition.Title} wizard",
        });
        if (_messages is not null)
        {
            _messages.Text = $"Saved alias '{name.Trim()}'.";
        }

        return true;
    }

    internal static bool AliasNameIsValid(string name) =>
        name.Trim().Length > 0 && name.Trim().All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');

    internal static bool IsOnPath(string command)
    {
        // Verb-Noun names are cmdlets; PowerShell resolves them without PATH.
        if (command.Length > 0 && char.IsUpper(command[0]) && command.Contains('-', StringComparison.Ordinal))
        {
            return true;
        }

        string[] extensions = OperatingSystem.IsWindows()
            ? [.. (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries), string.Empty]
            : [string.Empty];
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                try
                {
                    if (File.Exists(Path.Combine(dir, command + extension)))
                    {
                        return true;
                    }
                }
                catch (ArgumentException)
                {
                }
            }
        }

        return false;
    }

    private static string Root(string key) => key.Split('.')[0];

    private static HashSet<string> GlobalKeys(WizardDefinition definition) =>
        definition.Modes.Count == 0 ? [] : [.. definition.Sections.SelectMany(s => s.Options).Select(o => o.Id)];

    private static ListWrapper<string> Items(IEnumerable<string> items) => new(new ObservableCollection<string>(items));

    private void Load(WizardDefinition definition, string? input)
    {
        _definition = definition;
        _modeId = WizardSchema.ResolveMode(definition, null)?.Id;
        _values = new Dictionary<string, string>(StringComparer.Ordinal);
        _stashedModeValues.Clear();
        _replaceStart = _replaceLength = 0;
        var extras = string.Empty;
        if (!string.IsNullOrWhiteSpace(input))
        {
            var parsed = WizardEngine.Parse(definition, input);
            if (parsed.Matched)
            {
                _input = input;
                _replaceStart = parsed.Start;
                _replaceLength = parsed.Length;
                _modeId = parsed.ModeId ?? _modeId;
                _values = new Dictionary<string, string>(parsed.Values, StringComparer.Ordinal);
                extras = string.Join(' ', parsed.UnknownTokens);
            }
        }

        Title = $"{definition.Title} wizard  ·  Esc to close";
        BuildLayout(extras);
    }

    private void ReplaceContent(View content)
    {
        if (_content is not null)
        {
            Body.Remove(_content);
            _content.Dispose();
        }

        _editors.Clear();
        _formItems.Clear();
        _content = content;
        Body.Add(content);
    }

    private void ShowPicker()
    {
        var content = new View { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        var filter = new TextField { X = 16, Y = 0, Width = Dim.Fill() };
        _pickerList = new ListView { X = 0, Y = 2, Width = Dim.Fill(), Height = Dim.Fill() };
        content.Add(new Label { Text = "Find a wizard:", X = 0, Y = 0 }, filter, _pickerList);
        ReplaceContent(content);

        void Update()
        {
            _pickerMatches = Filter(Pickle.Wizards.All, filter.Text ?? string.Empty);
            _pickerList.SetSource(new ObservableCollection<string>(
                _pickerMatches.Select(w => $"{w.Title,-14} {w.Description}" + (w.WindowsOnly ? "  [Windows]" : string.Empty))));
            if (_pickerMatches.Count > 0)
            {
                _pickerList.SelectedItem = 0;
            }
        }

        filter.TextChanged += (_, _) => Update();
        filter.KeyDown += (_, key) =>
        {
            if (key == Key.CursorDown || key == Key.Enter)
            {
                if (key == Key.Enter && _pickerMatches.Count > 0)
                {
                    Pick(_pickerMatches[Math.Clamp(_pickerList.SelectedItem ?? 0, 0, _pickerMatches.Count - 1)]);
                }
                else
                {
                    _pickerList.SetFocus();
                }

                key.Handled = true;
            }
        };
        _pickerList.Accepting += (_, e) =>
        {
            if (_pickerList.SelectedItem is { } index && index >= 0 && index < _pickerMatches.Count)
            {
                e.Handled = true;
                Pick(_pickerMatches[index]);
            }
        };
        Update();
        filter.SetFocus();
    }

    private void BuildLayout(string extras)
    {
        var definition = _definition!;
        var content = new View { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };

        View last = new Label { Text = string.Empty, X = 0, Y = 0, Width = 0 };
        content.Add(last);
        if (definition.Modes.Count > 0)
        {
            var modeLabel = new Label { Text = "Mode:", X = 0, Y = 0 };
            _modeList = new DropDownList
            {
                X = Pos.Right(modeLabel) + 1,
                Y = 0,
                Width = 30,
                ReadOnly = true,
                Source = Items(definition.Modes.Select(m => m.Title)),
                Text = WizardSchema.ResolveMode(definition, _modeId)?.Title ?? string.Empty,
            };
            _modeList.TextChanged += (_, _) =>
            {
                if (!_loading && definition.Modes.FirstOrDefault(m => m.Title == _modeList.Text) is { } mode)
                {
                    SelectMode(mode.Id);
                }
            };
            content.Add(modeLabel, _modeList);
            last = _modeList;
        }

        if (definition.Presets.Count > 0)
        {
            var presetLabel = new Label { Text = "Preset:", X = last == _modeList ? Pos.Right(last) + 2 : 0, Y = 0 };
            var presets = new DropDownList
            {
                X = Pos.Right(presetLabel) + 1,
                Y = 0,
                Width = 36,
                ReadOnly = true,
                Source = Items(new[] { NoPreset }.Concat(definition.Presets.Select(p => p.Name))),
                Text = NoPreset,
            };
            presets.TextChanged += (_, _) =>
            {
                if (!_loading && definition.Presets.FirstOrDefault(p => p.Name == presets.Text) is { } preset)
                {
                    ApplyPreset(preset);
                }
            };
            content.Add(presetLabel, presets);
        }

        _install = new Button { Text = "Install " + definition.Command, X = Pos.AnchorEnd(), Y = 0, Visible = CanInstall() };
        _install.Accepting += (_, e) =>
        {
            e.Handled = true;
            InstallTool();
        };
        content.Add(_install);

        var description = new Label { X = 0, Y = 1, Width = Dim.Fill(), Height = 1 };
        _form = new View
        {
            X = 0,
            Y = 2,
            Width = Dim.Fill(),
            Height = Dim.Fill(7 + 1 + InputBox.Chrome),
            CanFocus = true,
            BorderStyle = LineStyle.Single,
            Title = "Options",
        };
        _form.ViewportSettings |= ViewportSettingsFlags.HasVerticalScrollBar;

        var extrasLabel = new Label { Text = "Extra arguments:", X = 0, Y = Pos.AnchorEnd(9) };
        _extras = InputBox.Boxed(new TextField { X = LabelWidth + 2, Y = Pos.AnchorEnd(8 + InputBox.Chrome), Width = Dim.Fill(1), Text = extras });
        _extras.TextChanged += (_, _) => Refresh();
        _help = new Label { X = 0, Y = Pos.AnchorEnd(7), Width = Dim.Fill(), Height = 1 };
        _messages = new Label { X = 0, Y = Pos.AnchorEnd(6), Width = Dim.Fill(), Height = 2 };
        var commandLabel = new Label { Text = "Command:", X = 0, Y = Pos.AnchorEnd(4) };
        _preview = new Label { X = 0, Y = Pos.AnchorEnd(3), Width = Dim.Fill(), Height = 2 };
        _preview.FrameChanged += (_, _) => ShowPreview();

        var run = new Button { Text = "_Run", X = 0, Y = Pos.AnchorEnd(1), IsDefault = false };
        var insert = new Button { Text = "_Insert", X = Pos.Right(run) + 1, Y = Pos.AnchorEnd(1) };
        var copy = new Button { Text = "_Copy", X = Pos.Right(insert) + 1, Y = Pos.AnchorEnd(1) };
        var alias = new Button { Text = "Save as _alias", X = Pos.Right(copy) + 1, Y = Pos.AnchorEnd(1) };
        Wire(run, RunCommand);
        Wire(insert, InsertCommand);
        Wire(copy, CopyCommand);
        Wire(alias, SaveAliasInteractive);

        content.Add(description, _form, extrasLabel, _extras, _help, _messages, commandLabel, _preview, run, insert, copy, alias);
        ReplaceContent(content);
        _descriptionLabel = description;
        RebuildForm();
    }

    private static void Wire(Button button, Action action) =>
        button.Accepting += (_, e) =>
        {
            e.Handled = true;
            action();
        };

    private void RebuildForm()
    {
        if (_definition is null || _form is null)
        {
            return;
        }

        _loading = true;
        try
        {
            foreach (var view in _form.SubViews.ToList())
            {
                _form.Remove(view);
                view.Dispose();
            }

            _editors.Clear();
            _formItems.Clear();
            var mode = WizardSchema.ResolveMode(_definition, _modeId);
            if (_modeList is not null && mode is not null)
            {
                _modeList.Text = mode.Title;
            }

            if (_descriptionLabel is not null)
            {
                _descriptionLabel.Text = string.IsNullOrEmpty(mode?.Description) ? _definition.Description : mode.Description;
            }

            var sections = mode is null ? _definition.Sections : _definition.Sections.Concat(mode.Sections);
            foreach (var section in sections)
            {
                if (section.Options.Count == 0)
                {
                    continue;
                }

                var header = new Label { Text = "── " + section.Title + " ──", X = 0, Width = Dim.Fill() };
                _form.Add(header);
                _formItems.Add(new FormItem(header, 1, null, section));
                foreach (var option in section.Options)
                {
                    var (row, height) = CreateRow(option);
                    _form.Add(row);
                    _formItems.Add(new FormItem(row, height, option, section));
                }
            }
        }
        finally
        {
            _loading = false;
        }

        Refresh();
    }

    private (View Row, int Height) CreateRow(WizardOption option)
    {
        var template = option.GetTemplate();
        const int Box = 1 + InputBox.Chrome;
        var listHeight = Math.Clamp(WizardEngine.Items(option, _values.GetValueOrDefault(option.Id)).Count + 1, 2, 5);
        var height = template is not null ? template.Placeholders.Count * Box
            : option.IsMultiValued() ? listHeight + InputBox.Chrome
            : option.Type == WizardOptionType.Flag ? 1
            : Box;

        // Boxed editors put their text on the box's middle row; labels line up with it.
        var labelY = height == 1 ? 0 : 1;
        var row = new View { X = 0, Width = Dim.Fill(), Height = height, CanFocus = true };
        var label = option.Label + (option.Required ? " *" : string.Empty);
        row.Add(new Label { Text = label.Length > LabelWidth ? label[..(LabelWidth - 1)] + "…" : label, X = 0, Y = labelY, Width = LabelWidth });
        var marker = new Label { Text = string.Empty, X = LabelWidth, Y = labelY, Width = 2, Id = "marker" };
        row.Add(marker);
        var x = LabelWidth + 2;
        var value = _values.GetValueOrDefault(option.Id);

        if (template is not null)
        {
            for (var i = 0; i < template.Placeholders.Count; i++)
            {
                var key = option.Id + "." + template.Placeholders[i];
                var optional = !template.RequiredPlaceholders.Contains(template.Placeholders[i]);
                row.Add(new Label { Text = Humanize(template.Placeholders[i]) + (optional ? " (opt.)" : string.Empty), X = x, Y = (i * Box) + 1, Width = 22 });
                var field = InputBox.Boxed(new TextField { X = x + 22, Y = i * Box, Width = Dim.Fill(1), Text = _values.GetValueOrDefault(key) ?? string.Empty });
                Bind(field, key, option);
                row.Add(field);
            }

            return (row, height);
        }

        View editor;
        switch (option.Type)
        {
            case WizardOptionType.Flag:
                var box = new CheckBox
                {
                    X = x,
                    Y = 0,
                    Text = option.Flag ?? string.Empty,
                    Value = WizardEngine.IsTrue(value) ? CheckState.Checked : CheckState.UnChecked,
                };
                box.ValueChanged += (_, e) => SetValue(option.Id, e.NewValue == CheckState.Checked ? "true" : null);
                editor = box;
                break;
            case WizardOptionType.Choice:
                var choices = option.Choices.Select(c => c.Value).ToList();
                if (value is not null && !choices.Contains(value))
                {
                    choices.Add(value);
                }

                var dropDown = InputBox.Boxed(new DropDownList
                {
                    X = x,
                    Y = 0,
                    Width = Dim.Fill(1),
                    Source = Items(new[] { string.Empty }.Concat(choices)),
                    Text = value ?? option.Default ?? string.Empty,
                });
                Bind(dropDown, option.Id, option);
                editor = dropDown;
                break;
            case WizardOptionType.List:
            case WizardOptionType.KeyValueList:
                editor = CreateListEditor(option.Id, value, x, listHeight);
                break;
            case WizardOptionType.Path:
                var path = InputBox.Boxed(new TextField { X = x, Y = 0, Width = Dim.Fill(5), Text = value ?? string.Empty });
                var browse = new Button { Text = "…", X = Pos.AnchorEnd(4), Y = 1, NoDecorations = true };
                browse.Accepting += (_, e) =>
                {
                    e.Handled = true;
                    Browse(path);
                };
                row.Add(browse);
                Bind(path, option.Id, option);
                editor = path;
                break;
            default:
                var text = InputBox.Boxed(new TextField { X = x, Y = 0, Width = Dim.Fill(1), Text = value ?? string.Empty });
                Bind(text, option.Id, option);
                editor = text;
                break;
        }

        editor.HasFocusChanged += (_, e) =>
        {
            if (e.NewValue)
            {
                OnFieldFocused(option, row);
            }
        };
        _editors[option.Id] = editor;
        row.Add(editor);
        return (row, height);
    }

    // Terminal.Gui 2.5 marks TextView obsolete in favour of a separate editor package; it is still the only
    // multi-line input that ships with it, and list values are one item per line.
#pragma warning disable CS0618
    private View CreateListEditor(string key, string? value, int x, int height)
    {
        var list = InputBox.Boxed(new TextView { X = x, Y = 0, Width = Dim.Fill(1), Text = value ?? string.Empty, TabKeyAddsTab = false }, height);
        list.ContentsChanged += (_, _) => SetValue(key, list.Text);
        return list;
    }
#pragma warning restore CS0618

    private void ShowPreview()
    {
        if (_preview is null)
        {
            return;
        }

        var line = Command?.CommandLine ?? string.Empty;
        var width = _preview.Viewport.Width;
        if (width <= 0 || line.Length <= width)
        {
            _preview.Text = line;
            return;
        }

        _preview.Text = line.Length <= width * 2 ? line[..width] + "\n" + line[width..] : line[..width] + "\n" + line[width..((width * 2) - 1)] + "…";
    }

    private void Bind(View field, string key, WizardOption option)
    {
        field.TextChanged += (_, _) => SetValue(key, field.Text);
        field.HasFocusChanged += (_, e) =>
        {
            if (e.NewValue && field.SuperView is { } row)
            {
                OnFieldFocused(option, row);
            }
        };
        _editors[key] = field;
    }

    private void SetValue(string key, string? value)
    {
        if (_loading)
        {
            return;
        }

        if (string.IsNullOrEmpty(value))
        {
            _values.Remove(key);
        }
        else
        {
            _values[key] = value;
        }

        Refresh();
    }

    private void Refresh()
    {
        if (_definition is null || _preview is null)
        {
            return;
        }

        var extras = _extras?.Text?.Trim();
        Command = WizardEngine.Build(_definition, _modeId, _values, string.IsNullOrEmpty(extras) ? null : [extras]);
        ShowPreview();

        var fieldErrors = WizardEngine.ValidateFields(_definition, _modeId, _values);
        foreach (var item in _formItems.Where(i => i.Option is not null))
        {
            var option = item.Option!;
            if (item.View.SubViews.FirstOrDefault(v => v.Id == "marker") is { } marker)
            {
                marker.Text = fieldErrors.ContainsKey(option.Id) ? "✖"
                    : option.GetWarning() is not null && Command.Warnings.Any(w => w.StartsWith(option.Label + ":", StringComparison.Ordinal)) ? "⚠"
                    : string.Empty;
            }
        }

        if (_messages is not null)
        {
            var lines = Command.Errors.Select(e => "✖ " + e).Concat(Command.Warnings.Select(w => "⚠ " + w)).ToList();
            _messages.Text = lines.Count switch
            {
                0 => string.Empty,
                <= 2 => string.Join('\n', lines),
                _ => lines[0] + "\n" + lines[1] + $"  (+{lines.Count - 2} more)",
            };
        }

        Relayout();
    }

    /// <summary>Hide options whose DependsOn doesn't hold (and headers of empty sections) and restack the rest.</summary>
    private void Relayout()
    {
        if (_definition is null || _form is null)
        {
            return;
        }

        var y = 0;
        for (var i = 0; i < _formItems.Count; i++)
        {
            var item = _formItems[i];
            bool visible;
            if (item.Option is null)
            {
                visible = _formItems.Skip(i + 1).TakeWhile(n => n.Option is not null)
                    .Any(n => WizardEngine.IsActive(_definition, _modeId, n.Option!, _values));
            }
            else
            {
                visible = WizardEngine.IsActive(_definition, _modeId, item.Option, _values);
            }

            item.View.Visible = visible;
            if (visible)
            {
                item.View.Y = y;
                y += item.Height;
            }
        }

        _form.SetContentHeight(y);
    }

    private void OnFieldFocused(WizardOption option, View row)
    {
        if (_help is not null)
        {
            var parts = new List<string>();
            if (option.Flag is { } flag)
            {
                parts.Add(flag);
            }

            if (!string.IsNullOrEmpty(option.Description))
            {
                parts.Add(option.Description);
            }

            if (option.Choices.Any(c => c.Label is not null))
            {
                parts.Add(string.Join(", ", option.Choices.Select(c => c.Label is null ? c.Value : $"{c.Value} = {c.Label}")));
            }

            if (option.Default is { } def)
            {
                parts.Add($"default {def}");
            }

            if (option.GetWarning() is { } warning)
            {
                parts.Add("⚠ " + warning);
            }

            _help.Text = string.Join("  ·  ", parts);
        }

        if (_form is not null)
        {
            var viewport = _form.Viewport;
            var top = row.Frame.Y;
            var bottom = top + row.Frame.Height;
            if (top < viewport.Y)
            {
                _form.Viewport = viewport with { Y = top };
            }
            else if (viewport.Height > 0 && bottom > viewport.Y + viewport.Height)
            {
                _form.Viewport = viewport with { Y = bottom - viewport.Height };
            }
        }
    }

    private void Browse(TextField target)
    {
        if (App is not { } app)
        {
            return;
        }

        using var dialog = new OpenDialog { Title = "Choose a file or folder", OpenMode = OpenMode.Mixed };
        app.Run(dialog);
        if (!dialog.Canceled && dialog.FilePaths.Count > 0)
        {
            target.Text = dialog.FilePaths[0];
        }
    }

    private void SaveAliasInteractive()
    {
        if (Command is null)
        {
            return;
        }

        var name = AskAliasName is not null ? AskAliasName() : PromptAliasName();
        if (name is null)
        {
            return;
        }

        if (!SaveAlias(name))
        {
            ShowError($"'{name}' is not a valid alias name (letters, digits, '-', '_' and '.').");
        }
    }

    private string? PromptAliasName()
    {
        if (App is not { } app)
        {
            return null;
        }

        using var dialog = new Dialog { Title = "Save as alias" };
        var field = InputBox.Boxed(new TextField { X = 0, Y = 1, Width = 40, Text = _definition?.Id ?? string.Empty });
        dialog.Add(new Label { Text = "Alias name:", X = 0, Y = 0 }, field);
        dialog.AddButton(new Button { Text = "_Cancel" });
        dialog.AddButton(new Button { Text = "_Save" });
        app.Run(dialog);
        return dialog.Result == 1 ? field.Text : null;
    }

    private bool CanInstall() =>
        _definition is { } definition
        && Package(definition) is not null
        && !_toolExists(definition.Command);

    /// <summary>Tests answer the install dialog through this instead of a modal.</summary>
    internal Func<ToolPackage, ToolInstallOptions?>? AskInstallOptions { get; set; }

    /// <summary>The package for the wizard's tool: the wizard's own winget id first, then the tool catalog.</summary>
    private ToolPackage? Package(WizardDefinition definition)
    {
        if (Pickle.Services.Get<IToolInstaller>() is not { IsSupported: true } installer)
        {
            return null;
        }

        var known = installer.Find(definition.Command);
        return definition.WingetId is { Length: > 0 } id && !string.Equals(known?.WingetId, id, StringComparison.OrdinalIgnoreCase)
            ? new ToolPackage(definition.Command, id, definition.Title, known?.InstallDirs ?? [])
            : known;
    }

    internal void InstallTool()
    {
        if (_definition is null || Package(_definition) is not { } package || Pickle.Services.Get<IToolInstaller>() is not { } installer)
        {
            return;
        }

        var addToPath = Pickle.Config.Current.Shell.AddInstalledToolsToPath;
        var options = AskInstallOptions is not null ? AskInstallOptions(package)
            : App is { } app ? ToolInstallDialog.Show(app, Schemes, package, addToPath)
            : new ToolInstallOptions(ToolInstallScope.User, addToPath);
        if (options is null)
        {
            return;
        }

        RunInBackground(
            ct => installer.InstallAsync(package, options, null, ct),
            result =>
            {
                if (_messages is not null)
                {
                    _messages.Text = result.Success ? result.Message : $"Install failed: {result.Message}";
                }

                if (result.Success && _install is not null)
                {
                    _install.Visible = false;
                }
            },
            "installing…");
    }

    private static string Humanize(string name)
    {
        var chars = new List<char>();
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
            {
                chars.Add(' ');
            }

            chars.Add(i == 0 ? char.ToUpperInvariant(name[i]) : char.ToLowerInvariant(name[i]));
        }

        return new string([.. chars]);
    }

    private sealed record FormItem(View View, int Height, WizardOption? Option, WizardSection Section);
}
