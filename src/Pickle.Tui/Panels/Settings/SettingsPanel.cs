using System.Collections.ObjectModel;
using Pickle.Abstractions;
using Pickle.Tui.Widgets;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Pickle.Tui.Panels.Settings;

/// <summary>
/// Settings (Alt+,): categories on the left, fields generated from the config classes on the right. Every change is
/// saved immediately through <see cref="IConfigStore.SetValue"/> (text fields on Enter or when leaving them); theme
/// changes preview live. The "Key bindings" category edits <c>keyBindings</c>. The argument may name a category or
/// a setting path (e.g. <c>editor.autosuggestions</c>).
/// </summary>
public sealed class SettingsPanel : PanelWindow
{
    private readonly ListView _categories;
    private readonly FrameView _content;
    private readonly Label _status;
    private readonly IReadOnlyList<SettingField> _fields;
    private readonly Dictionary<string, View> _editors = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<View> _focusOrder = [];
    private FilterableList<BindingRow>? _bindings;
    private string _category = string.Empty;

    public SettingsPanel(PanelContext context)
        : base(context, "Settings")
    {
        _fields = SettingsModel.Fields(Pickle);
        _categories = new ListView
        {
            X = 0,
            Y = 0,
            Width = 18,
            Height = Dim.Fill(1),
        };
        _categories.SetSource(new ObservableCollection<string>(SettingsModel.Categories));
        _content = new FrameView { X = Pos.Right(_categories) + 1, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(1) };
        var path = new Label { Text = $"Config: {Pickle.Paths.ConfigFile}", X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Percent(60), Height = 1 };
        _status = new Label { X = Pos.Right(path) + 1, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1 };
        Body.Add(_categories, _content, path, _status);

        _categories.KeyDown += (_, key) =>
        {
            if (key == Key.CursorRight && _focusOrder.FirstOrDefault() is { } first)
            {
                first.SetFocus();
                key.Handled = true;
            }
        };
        _categories.ValueChanged += (_, e) =>
        {
            if (e.NewValue is { } index && index >= 0 && index < SettingsModel.Categories.Count)
            {
                ShowCategory(SettingsModel.Categories[index]);
            }
        };

        var (category, focusPath) = ResolveArgument(context.Argument);
        _categories.SelectedItem = Math.Max(0, IndexOf(category));
        ShowCategory(SettingsModel.Categories[_categories.SelectedItem ?? 0]);
        if (focusPath is not null && _editors.TryGetValue(focusPath, out var editor))
        {
            editor.SetFocus();
        }
        else
        {
            _categories.SetFocus();
        }
    }

    public string Category => _category;

    /// <summary>The editor view for a setting path in the current category (CheckBox, TextField or DropDownList).</summary>
    internal View? EditorFor(string path) => _editors.GetValueOrDefault(path);

    internal FilterableList<BindingRow>? Bindings => _bindings;

    internal string StatusText => _status.Text ?? string.Empty;

    internal void ShowCategory(string category)
    {
        if (string.Equals(category, _category, StringComparison.Ordinal))
        {
            return;
        }

        _category = category;
        foreach (var old in _content.RemoveAll().ToList())
        {
            old.Dispose();
        }

        _editors.Clear();
        _focusOrder.Clear();
        _bindings = null;
        _content.Title = category;
        if (category == SettingsModel.KeyBindingsCategory)
        {
            BuildKeyBindings();
        }
        else
        {
            BuildFields(category);
        }

        _content.SetNeedsDraw();
    }

    internal string? Save(SettingField field, string value)
    {
        var error = SettingsModel.Write(Pickle, field, value);
        SetStatus(error ?? $"✓ saved {field.Path}", error is not null);
        return error;
    }

    private static int IndexOf(string category) =>
        SettingsModel.Categories.ToList().FindIndex(c => string.Equals(c, category, StringComparison.OrdinalIgnoreCase));

    private (string Category, string? Path) ResolveArgument(string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            return (SettingsModel.ThemeCategory, null);
        }

        var text = argument.Trim();
        if (IndexOf(text) >= 0)
        {
            return (text, null);
        }

        if (_fields.FirstOrDefault(f => string.Equals(f.Path, text, StringComparison.OrdinalIgnoreCase)) is { } field)
        {
            return (field.Category, field.Path);
        }

        var section = _fields.FirstOrDefault(f => f.Path.StartsWith(text + ".", StringComparison.OrdinalIgnoreCase));
        return (section?.Category ?? SettingsModel.ThemeCategory, null);
    }

    private void BuildFields(string category)
    {
        var row = 0;
        foreach (var field in _fields.Where(f => f.Category == category))
        {
            var label = new Label { Text = field.Label, X = 1, Y = row, Width = 30 };
            var editor = CreateEditor(field);
            editor.X = 32;
            editor.Y = row;
            _content.Add(label, editor);
            _editors[field.Path] = editor;
            _focusOrder.Add(editor);
            LeftReturnsToCategories(editor);
            row++;
        }

        if (category == SettingsModel.ThemeCategory)
        {
            _content.Add(new Label { Text = "↑/↓ in the list previews themes live.", X = 1, Y = row + 1, Width = Dim.Fill(1) });
        }
    }

    private View CreateEditor(SettingField field)
    {
        var current = SettingsModel.Read(Pickle, field);
        switch (field.Kind)
        {
            case SettingKind.Bool:
                var check = new CheckBox { Text = string.Empty, Value = current == "true" ? CheckState.Checked : CheckState.UnChecked };
                check.ValueChanged += (_, e) => Save(field, e.NewValue == CheckState.Checked ? "true" : "false");
                return check;
            case SettingKind.Choice:
                var choices = field.Choices.ToList();
                var dropDown = new DropDownList
                {
                    Width = Math.Max(16, choices.Select(c => c.Length).DefaultIfEmpty(10).Max() + 4),
                    Source = new ListWrapper<string>(new ObservableCollection<string>(choices)),
                    Text = current,
                    ReadOnly = true,
                };
                dropDown.SetScheme(Schemes.Input);
                dropDown.ValueChanged += (_, e) =>
                {
                    var value = e.NewValue ?? string.Empty;
                    if (choices.Contains(value, StringComparer.OrdinalIgnoreCase)
                        && !string.Equals(value, SettingsModel.Read(Pickle, field), StringComparison.OrdinalIgnoreCase))
                    {
                        Save(field, value);
                    }
                };
                return dropDown;
            default:
                var text = new TextField { Width = Dim.Fill(2), Text = current };
                text.SetScheme(Schemes.Input);
                var committed = current;
                void Commit()
                {
                    var value = text.Text ?? string.Empty;
                    if (value != committed && Save(field, value) is null)
                    {
                        committed = SettingsModel.Read(Pickle, field);
                        text.Text = committed;
                    }
                }

                text.KeyDown += (_, key) =>
                {
                    if (key == Key.Enter)
                    {
                        Commit();
                        key.Handled = true;
                    }
                };
                text.HasFocusChanged += (_, e) =>
                {
                    if (!e.NewValue)
                    {
                        Commit();
                    }
                };
                return text;
        }
    }

    /// <summary>Left goes back to the category list, except while it still moves the cursor inside a text field.</summary>
    private void LeftReturnsToCategories(View view)
    {
        view.KeyDown += (_, key) =>
        {
            if (key == Key.CursorLeft && (view is not TextField field || field.ReadOnly || field.InsertionPoint == 0))
            {
                _categories.SetFocus();
                key.Handled = true;
            }
        };

        foreach (var sub in view.SubViews)
        {
            LeftReturnsToCategories(sub);
        }
    }

    private void SetStatus(string text, bool error)
    {
        _status.Text = text;
        _status.SetScheme(new Terminal.Gui.Drawing.Scheme(error ? Schemes.ErrorText : Schemes.Success));
    }

    // ───────────── Key bindings ─────────────

    private void BuildKeyBindings()
    {
        var help = new Label { Text = "Enter change action · F2 add · Del unbind", X = 1, Y = 0, Width = Dim.Fill(1) };
        _bindings = new FilterableList<BindingRow>(r => $"{r.Chord,-24}{r.Action}")
        {
            X = 0,
            Y = 1,
            Height = Dim.Fill(),
            Detail = r => r.Description,
            Hint = r => r.Custom ? "custom" : null,
            Schemes = Schemes,
        };
        _content.Add(help, _bindings);
        _focusOrder.Add(_bindings.Filter);
        LeftReturnsToCategories(_bindings);
        _bindings.ItemAccepted += (_, row) => EditBinding(row.Chord);
        _bindings.Filter.KeyDown += (_, key) =>
        {
            if (key == Key.F2 || key == Key.InsertChar)
            {
                AddBinding();
                key.Handled = true;
            }
            else if (key == Key.Delete && _bindings.FilterText.Length == 0 && _bindings.Selected is { } row)
            {
                RemoveBinding(row.Chord);
                key.Handled = true;
            }
        };
        _bindings.SetItems(BindingRows());
    }

    internal IReadOnlyList<BindingRow> BindingRows()
    {
        var registry = Pickle.KeyBindings;
        var custom = Pickle.Config.Current.KeyBindings.ToDictionary(kv => Normalize(kv.Key), kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var rows = registry.Bindings
            .Select(b => new BindingRow(b.Key, b.Value, registry.GetAction(b.Value)?.Description, custom.ContainsKey(b.Key)))
            .ToList();
        rows.AddRange(custom
            .Where(c => c.Value is "none" or "" && !registry.Bindings.ContainsKey(c.Key))
            .Select(c => new BindingRow(c.Key, "none", "unbound", true)));
        return [.. rows.OrderBy(r => r.Action, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Chord, StringComparer.OrdinalIgnoreCase)];
    }

    internal void SetBinding(string chord, string action)
    {
        var normalized = Normalize(chord);
        Pickle.Config.Update(c =>
        {
            foreach (var key in c.KeyBindings.Keys.Where(k => string.Equals(Normalize(k), normalized, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                c.KeyBindings.Remove(key);
            }

            c.KeyBindings[normalized] = action;
        });
        if (action == "none")
        {
            Pickle.KeyBindings.Unbind(normalized);
        }
        else
        {
            Pickle.KeyBindings.Bind(normalized, action);
        }

        SetStatus($"✓ {normalized} → {action}", false);
        _bindings?.SetItems(BindingRows());
        _bindings?.Select(_bindings.VisibleItems.FirstOrDefault(r => r.Chord == normalized) ?? default!);
    }

    internal void RemoveBinding(string chord) => SetBinding(chord, "none");

    private static string Normalize(string chord) => KeyChord.TryParse(chord, out var c) ? c.ToString() : chord;

    private void AddBinding()
    {
        if (App is not { } app)
        {
            return;
        }

        var chord = ChordDialog.Show(app, Schemes);
        if (chord is null)
        {
            return;
        }

        EditBinding(chord);
    }

    private void EditBinding(string chord)
    {
        var action = Pick(
            $"Action for {chord}",
            Pickle.KeyBindings.Actions.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase),
            a => a.Name,
            a => KeyHints.Describe(Pickle.KeyBindings, a.Name));
        if (action is not null)
        {
            SetBinding(chord, action.Name);
        }
    }

    internal sealed record BindingRow(string Chord, string Action, string? Description, bool Custom);
}
