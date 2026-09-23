using System.Text;
using Pickle.Abstractions;
using Pickle.Core.Contracts;

namespace Pickle.Core.Input;

/// <summary>
/// FOUNDATION PLACEHOLDER (workstream W1 replaces this file): a minimal single-line editor so the shell works
/// end to end. Supports typing, Backspace/Delete, Left/Right/Home/End, Up/Down history, Enter, Ctrl+C, Ctrl+D,
/// Esc, and key-bound actions from the registry (panels etc.).
/// </summary>
public sealed class LineEditor : ILineEditor, IEditorBuffer, IRuntimeComponent
{
    private readonly PickleRuntime _runtime;
    private readonly StringBuilder _buffer = new();
    private int _cursor;
    private bool _accepted;
    private string _promptLastLine = string.Empty;
    private IEditorOverlay? _overlay;

    public LineEditor(PickleRuntime runtime) => _runtime = runtime;

    public string Text => _buffer.ToString();

    public int Cursor => _cursor;

    public string? Suggestion => null;

    public void Initialize()
    {
        DefaultKeyBindings.Apply(_runtime);
        var registry = _runtime.KeyBindingRegistry;
        void Register(string name, string description, Action action) =>
            registry.RegisterAction(name, description, (_, _) =>
            {
                action();
                return ValueTask.CompletedTask;
            });

        Register(EditorActionNames.AcceptLine, "Run the command", Accept);
        Register(EditorActionNames.CancelLine, "Cancel the current line", Cancel);
        Register(EditorActionNames.ClearLine, "Clear the current line", () => Replace(string.Empty, 0));
        Register(EditorActionNames.BackwardChar, "Move left", BackwardChar);
        Register(EditorActionNames.ForwardChar, "Move right", ForwardChar);
        Register(EditorActionNames.BeginningOfLine, "Move to start of line", Home);
        Register(EditorActionNames.EndOfLine, "Move to end of line", End);
        Register(EditorActionNames.BackwardDeleteChar, "Delete previous character", Backspace);
        Register(EditorActionNames.DeleteChar, "Delete next character", Delete);
        Register(EditorActionNames.ExitIfEmpty, "Exit when the line is empty", () => { });
    }

    public string? ReadLine(PromptRender prompt, PromptContext promptContext, CancellationToken cancellationToken = default)
    {
        _buffer.Clear();
        _cursor = 0;
        _accepted = false;
        var promptLines = prompt.Left.Split('\n');
        for (var i = 0; i < promptLines.Length - 1; i++)
        {
            _runtime.Terminal.Write(promptLines[i] + "\n");
        }

        _promptLastLine = promptLines[^1];
        var historyIndex = _runtime.History.Entries.Count;
        Render();

        while (!_accepted)
        {
            DrainRequests();
            var key = _runtime.Terminal.ReadKey(cancellationToken);
            var action = _runtime.KeyBindingRegistry.Lookup(key);
            if (action is not null && _runtime.KeyBindingRegistry.GetAction(action) is { } info
                && action is not (EditorActionNames.HistoryPrevious or EditorActionNames.HistoryNext))
            {
                info.Handler(this, cancellationToken).AsTask().GetAwaiter().GetResult();
                if (_accepted)
                {
                    break;
                }

                if (action == EditorActionNames.ExitIfEmpty && _buffer.Length == 0)
                {
                    _runtime.Terminal.Write("\n");
                    return null;
                }

                Render();
                continue;
            }

            switch (key.Key)
            {
                case ConsoleKey.UpArrow when historyIndex > 0:
                    historyIndex--;
                    Replace(_runtime.History.Entries[historyIndex].CommandLine, int.MaxValue);
                    break;
                case ConsoleKey.DownArrow when historyIndex < _runtime.History.Entries.Count:
                    historyIndex++;
                    Replace(historyIndex < _runtime.History.Entries.Count ? _runtime.History.Entries[historyIndex].CommandLine : string.Empty, int.MaxValue);
                    break;
                default:
                    if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                    {
                        Insert(key.KeyChar.ToString());
                    }

                    break;
            }

            Render();
        }

        _runtime.Terminal.Write("\n");
        return Text;
    }

    public string? ReadSimpleLine(string prompt, bool mask)
    {
        var sb = new StringBuilder();
        _runtime.Terminal.Write(prompt);
        while (true)
        {
            var key = _runtime.Terminal.ReadKey();
            if (key.Key == ConsoleKey.Enter)
            {
                _runtime.Terminal.Write("\n");
                return sb.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                {
                    sb.Length--;
                    _runtime.Terminal.Write("\b \b");
                }

                continue;
            }

            if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                _runtime.Terminal.Write("\n");
                return null;
            }

            if (!char.IsControl(key.KeyChar))
            {
                sb.Append(key.KeyChar);
                _runtime.Terminal.Write(mask ? "*" : key.KeyChar.ToString());
            }
        }
    }

    // ───────────── IEditorBuffer ─────────────

    public void Insert(string text)
    {
        _buffer.Insert(_cursor, text);
        _cursor += text.Length;
    }

    public void Replace(string text, int cursor)
    {
        _buffer.Clear().Append(text);
        _cursor = Math.Clamp(cursor, 0, _buffer.Length);
    }

    public void Accept() => _accepted = true;

    public void Redraw() => Render();

    public void OpenOverlay(IEditorOverlay overlay) => _overlay = overlay;

    public void CloseOverlay() => _overlay = null;

    public void ShowPanel(string panelId, string? argument = null)
    {
        var host = _runtime.ServiceRegistry.Get<IPanelHost>();
        if (host is null)
        {
            return;
        }

        _runtime.Terminal.Write("\r" + Ansi.ClearToEndOfLine);
        var result = host.Show(panelId, argument, Text);
        ApplyPanelResult(this, result);
        Render();
    }

    internal static void ApplyPanelResult(IEditorBuffer buffer, PanelResult? result)
    {
        switch (result?.Kind)
        {
            case PanelResultKind.InsertText:
                buffer.Insert(result.Text);
                break;
            case PanelResultKind.ReplaceInput:
                buffer.Replace(result.Text, result.Text.Length);
                break;
            case PanelResultKind.RunCommand:
                buffer.Replace(result.Text, result.Text.Length);
                buffer.Accept();
                break;
            case PanelResultKind.ChangeDirectory:
                var cmd = "Set-Location -LiteralPath '" + result.Text.Replace("'", "''", StringComparison.Ordinal) + "'";
                buffer.Replace(cmd, cmd.Length);
                buffer.Accept();
                break;
        }
    }

    // ───────────── Built-in actions used by DefaultKeyBindings ─────────────

    internal void BackwardChar() => _cursor = Math.Max(0, _cursor - 1);

    internal void ForwardChar() => _cursor = Math.Min(_buffer.Length, _cursor + 1);

    internal void Home() => _cursor = 0;

    internal void End() => _cursor = _buffer.Length;

    internal void Backspace()
    {
        if (_cursor > 0)
        {
            _buffer.Remove(_cursor - 1, 1);
            _cursor--;
        }
    }

    internal void Delete()
    {
        if (_cursor < _buffer.Length)
        {
            _buffer.Remove(_cursor, 1);
        }
    }

    internal void Cancel()
    {
        _runtime.Terminal.Write(Ansi.Colorize("^C", _runtime.Themes.Current.Ui.Muted));
        Replace(string.Empty, 0);
        _accepted = true;
    }

    private void DrainRequests()
    {
        var changed = false;
        while (_runtime.Engine.EditorRequests.TryDequeue(out var request))
        {
            if (request.Replace)
            {
                Replace(request.Text, request.Text.Length);
            }
            else
            {
                Insert(request.Text);
            }

            changed = true;
        }

        if (changed)
        {
            Render();
        }
    }

    private void Render()
    {
        var text = Text;
        var tail = TextWidth.VisibleWidth(text[_cursor..]);
        _runtime.Terminal.Write("\r" + _promptLastLine + text + Ansi.ClearToEndOfLine + Ansi.CursorBack(tail));
    }
}
