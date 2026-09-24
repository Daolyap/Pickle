using System.Collections.ObjectModel;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Pickle.Tui.Panels.Windows;

/// <summary>A titled, scrollable, read-only block of text (lines are wrapped to the pane width).</summary>
public sealed class TextPane : FrameView
{
    private readonly ListView _list;
    private readonly ObservableCollection<string> _lines = [];
    private string _content = string.Empty;

    public TextPane()
    {
        _list = new ListView { Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        _list.SetSource(_lines);
        Add(_list);
        FrameChanged += (_, _) => Render();
    }

    public string Content
    {
        get => _content;
        set
        {
            _content = value ?? string.Empty;
            Render();
        }
    }

    private void Render()
    {
        var width = Math.Max(20, Viewport.Width - 1);
        _lines.Clear();
        foreach (var line in _content.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var rest = line;
            do
            {
                var take = Math.Min(width, rest.Length);
                _lines.Add(rest[..take]);
                rest = rest[take..];
            }
            while (rest.Length > 0);
        }

        if (_lines.Count > 0 && _list.SelectedItem is null)
        {
            _list.SelectedItem = 0;
        }
    }
}
