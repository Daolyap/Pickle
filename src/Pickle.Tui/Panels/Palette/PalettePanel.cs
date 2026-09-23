using Pickle.Abstractions;
using Pickle.Tui.Panels.Settings;
using Pickle.Tui.Widgets;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Pickle.Tui.Panels.Palette;

/// <summary>Shared between the palette panel and its key action: an editor action picked in the palette runs after it closes.</summary>
public sealed class PaletteState
{
    private EditorActionInfo? _pending;

    public EditorActionInfo? PendingAction
    {
        get => Volatile.Read(ref _pending);
        set => Volatile.Write(ref _pending, value);
    }

    public EditorActionInfo? TakePendingAction() => Interlocked.Exchange(ref _pending, null);
}

/// <summary>
/// The command palette (F1 / Ctrl+P): fuzzy search over panels, wizards, <c>pk</c> commands, editor actions (with
/// their chords), themes and settings. Enter runs the selection; Esc closes.
/// </summary>
public sealed class PalettePanel : PanelWindow
{
    private readonly PaletteState _state;
    private readonly FilterableList<PaletteItem> _list;
    private readonly Label _description;

    public PalettePanel(PanelContext context, PaletteState state)
        : base(context, "Command palette")
    {
        _state = state;
        (Pickle.Services.Get<IPanelHost>() as PanelHost)?.ListPanels?.Sync();

        _list = new FilterableList<PaletteItem>(i => i.Title)
        {
            Category = i => i.Category,
            Hint = i => i.Chord,
            Detail = i => i.Description,
            Keywords = i => i.Keywords,
            Height = Dim.Fill(1),
            Schemes = Schemes,
        };
        _description = new Label { X = 1, Y = Pos.AnchorEnd(1), Width = Dim.Fill(1), Height = 1 };
        Body.Add(_list, _description);

        _list.Accepted += (_, item) => Run(item);
        _list.SelectionChanged += (_, item) => _description.Text = item is null ? string.Empty : Describe(item);
        AddHint(Key.Enter, "Run", () =>
        {
            if (_list.Selected is { } item)
            {
                Run(item);
            }
        });

        _list.SetItems(PaletteItem.Collect(Pickle));
        if (!string.IsNullOrEmpty(context.Argument))
        {
            _list.FilterText = context.Argument;
        }

        _description.Text = _list.Selected is { } first ? Describe(first) : string.Empty;
        _list.Filter.SetFocus();
    }

    internal FilterableList<PaletteItem> List => _list;

    internal void Run(PaletteItem item)
    {
        switch (item.Kind)
        {
            case PaletteKind.Panel:
                if (!OpenPanel(item.Target))
                {
                    ShowError($"Panel '{item.Target}' is not available.");
                }

                break;
            case PaletteKind.Wizard:
                if (Pickle.Panels.Get("wizard") is null || !OpenPanel("wizard", item.Target))
                {
                    ShowError("The wizard panel is not available.");
                }

                break;
            case PaletteKind.Command:
                Complete(new PanelResult(PanelResultKind.ReplaceInput, $"pk {item.Target} "));
                break;
            case PaletteKind.Action:
                _state.PendingAction = Pickle.KeyBindings.GetAction(item.Target);
                Close();
                break;
            case PaletteKind.Theme:
                try
                {
                    Pickle.Themes.Apply(item.Target);
                }
                catch (ArgumentException ex)
                {
                    ShowError(ex.Message);
                    return;
                }

                Close();
                break;
            case PaletteKind.Setting:
                if (!OpenPanel(SettingsPanelPlugin.PanelId, item.Target))
                {
                    ShowError("The settings panel is not available.");
                }

                break;
        }
    }

    private static string Describe(PaletteItem item)
    {
        var text = $"{item.Category}: {item.Title}";
        if (!string.IsNullOrWhiteSpace(item.Description))
        {
            text += " — " + item.Description;
        }

        if (item.Kind is PaletteKind.Panel or PaletteKind.Action && !string.IsNullOrEmpty(item.Chord))
        {
            text += $"  [{item.Chord}]";
        }

        return text;
    }
}
