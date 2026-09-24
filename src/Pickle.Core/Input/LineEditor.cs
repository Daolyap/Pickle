using System.Management.Automation.Language;
using System.Text;
using Pickle.Abstractions;
using Pickle.Core.Contracts;
using Pickle.Core.Render;
using Pickle.Core.Syntax;

namespace Pickle.Core.Input;

/// <summary>
/// The interactive multi-line editor: prompt + syntax-highlighted input + autosuggestion ghost text + overlay rows,
/// rendered as a diffed <see cref="Frame"/>. Keys go to the open overlay first, then to the key binding registry
/// (every editing action in <see cref="EditorActionNames"/> is registered here), then self-insert.
/// Keys that arrive in a burst (a paste) are inserted literally, including newlines, and never execute mid-burst.
/// </summary>
public sealed partial class LineEditor : ILineEditor, IEditorBuffer, IRuntimeComponent
{
    private static readonly TimeSpan IdlePoll = TimeSpan.FromMilliseconds(50);

    // A paste can reach us in several chunks; an Enter that ends a chunk waits this long for the rest.
    private static readonly TimeSpan PasteGrace = TimeSpan.FromMilliseconds(15);

    private readonly PickleRuntime _runtime;
    private readonly UndoStack _undo = new();
    private FrameRenderer? _renderer;
    private IClipboard? _clipboard;

    private string _text = string.Empty;
    private int _cursor;
    private int? _anchor;
    private IEditorOverlay? _overlay;
    private PromptRender _prompt = new(string.Empty, null, string.Empty);
    private PromptContext? _promptContext;
    private bool _reading;
    private Outcome _outcome;
    private string? _suggestion;
    private string? _suggestionFor;
    private int? _preferredColumn;
    private char? _pendingHighSurrogate;
    private bool _burst;
    private bool _inBurst;
    private HistoryNavigator? _history;
    private bool _keepHistory;
    private int _renderedHighlightVersion;
    private (int Width, int Height) _renderedSize;
    private volatile bool _promptRefreshPending;

    public LineEditor(PickleRuntime runtime) => _runtime = runtime;

    private enum Outcome
    {
        None,
        Accept,
        Cancel,
        EndOfFile,
    }

    public string Text => _text;

    public int Cursor => _cursor;

    public string? Suggestion
    {
        get
        {
            UpdateSuggestion();
            return _suggestion;
        }
    }

    /// <summary>The currently open overlay (for tests and diagnostics).</summary>
    internal IEditorOverlay? Overlay => _overlay;

    internal (int Start, int End)? Selection =>
        _anchor is { } anchor && anchor != _cursor ? (Math.Min(anchor, _cursor), Math.Max(anchor, _cursor)) : null;

    internal IClipboard ClipboardService
    {
        get => _clipboard ??= Clipboard.Create(_runtime);
        set => _clipboard = value;
    }

    private FrameRenderer Renderer => _renderer ??= new FrameRenderer(_runtime.Terminal);

    private EditorSettings Settings => _runtime.Config.Current.Editor;

    private Theme Theme => _runtime.Themes.Current;

    public void Initialize()
    {
        DefaultKeyBindings.Apply(_runtime);
        RegisterActions();
        HookPromptRefresh();
    }

    // The prompt engine raises SegmentsRefreshed (on a background thread) when a slow segment finishes after the
    // prompt was drawn. Bound by name so any EventHandler/EventHandler<T>/Action signature works.
    private void HookPromptRefresh()
    {
        var prompt = _runtime.Prompt;
        if (prompt.GetType().GetEvent("SegmentsRefreshed") is not { EventHandlerType: { } type } evt)
        {
            return;
        }

        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var handler = Delegate.CreateDelegate(type, this, GetType().GetMethod(nameof(OnPromptRefreshed), flags)!, throwOnBindFailure: false)
            ?? Delegate.CreateDelegate(type, this, GetType().GetMethod(nameof(OnPromptRefreshedNoArgs), flags)!, throwOnBindFailure: false);
        if (handler is not null)
        {
            evt.AddEventHandler(prompt, handler);
        }
    }

    private void OnPromptRefreshed(object? sender, object? args) => _promptRefreshPending = true;

    private void OnPromptRefreshedNoArgs() => _promptRefreshPending = true;

    // ───────────── ReadLine ─────────────

    public string? ReadLine(PromptRender prompt, PromptContext promptContext, CancellationToken cancellationToken = default)
    {
        _prompt = prompt;
        _promptContext = promptContext;
        _text = string.Empty;
        _cursor = 0;
        _anchor = null;
        _overlay = null;
        _outcome = Outcome.None;
        _suggestion = null;
        _suggestionFor = null;
        _preferredColumn = null;
        _pendingHighSurrogate = null;
        _history = null;
        _burst = false;
        _promptRefreshPending = false;
        _undo.Clear();
        _reading = true;
        try
        {
            StartOnFreshLine();
            Renderer.Reset();
            DrainRequests();
            Render();
            DrainPendingPanels();
            while (_outcome == Outcome.None)
            {
                var key = NextKey(cancellationToken);
                ProcessKey(key, cancellationToken);
                if (_outcome == Outcome.None && !_burst)
                {
                    DrainRequests();
                    Render();

                    // A key handler that ran a `pk` command may have asked for a panel.
                    DrainPendingPanels();
                }
            }

            return Finish();
        }
        finally
        {
            _reading = false;
            _overlay = null;
        }
    }

    private ConsoleKeyInfo NextKey(CancellationToken cancellationToken)
    {
        while (!_runtime.Terminal.WaitForInput(IdlePoll, cancellationToken))
        {
            OnIdle();
        }

        return _runtime.Terminal.ReadKey(cancellationToken);
    }

    private void OnIdle()
    {
        var terminal = _runtime.Terminal;
        var changed = DrainRequests();
        changed |= RefreshPrompt();
        changed |= (terminal.Width, terminal.Height) != _renderedSize;
        changed |= _runtime.Highlighter is SyntaxHighlighter highlighter && highlighter.Version != _renderedHighlightVersion;
        if (changed)
        {
            Render();
        }
    }

    // Only the main prompt: nested and debugger prompts are read while a pipeline is executing.
    private bool RefreshPrompt()
    {
        if (!_promptRefreshPending || _runtime.Engine.IsExecuting || _promptContext is not { } context)
        {
            return false;
        }

        _promptRefreshPending = false;
        try
        {
            _prompt = _runtime.Prompt.Render(context);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _runtime.Log.Warn("editor", "prompt refresh failed", ex);
            return false;
        }
    }

    private void ProcessKey(ConsoleKeyInfo key, CancellationToken cancellationToken)
    {
        // Requests queued while we waited apply before the key (terminals that can't poll never report idle).
        DrainRequests();
        var terminal = _runtime.Terminal;
        var more = terminal.KeyAvailable;
        if (!more && _burst && key.Key == ConsoleKey.Enter)
        {
            more = terminal.WaitForInput(PasteGrace, cancellationToken) && terminal.KeyAvailable;
        }

        _inBurst = more || _burst;
        _burst = more;
        _keepHistory = false;

        var overlay = _overlay;
        var textBefore = _text;
        var cursorBefore = _cursor;
        if (overlay is not null)
        {
            switch (OverlayHandleKey(overlay, key))
            {
                case OverlayKeyResult.Handled:
                    _history = null;
                    return;
                case OverlayKeyResult.Close:
                    if (ReferenceEquals(_overlay, overlay))
                    {
                        _overlay = null;
                    }

                    _history = null;
                    return;
            }
        }

        if (more && IsLiteralPasteKey(key))
        {
            InsertLiteral(key);
        }
        else
        {
            Dispatch(key, cancellationToken);
        }

        if (!_keepHistory)
        {
            _history = null;
        }

        if (_overlay is { } open && ReferenceEquals(open, overlay) && (_text != textBefore || _cursor != cursorBefore))
        {
            try
            {
                open.OnBufferChanged(this);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                OverlayFailed(open, ex);
            }
        }
    }

    private void Dispatch(ConsoleKeyInfo key, CancellationToken cancellationToken)
    {
        var name = _runtime.KeyBindingRegistry.Lookup(key);
        if (name is not null && _runtime.KeyBindingRegistry.GetAction(name) is { } action)
        {
            RunAction(action, cancellationToken);
            return;
        }

        if (IsPrintable(key))
        {
            SelfInsert(key.KeyChar);
            return;
        }

        Bell();
    }

    private void RunAction(EditorActionInfo action, CancellationToken cancellationToken)
    {
        try
        {
            var pending = action.Handler(this, cancellationToken);
            if (!pending.IsCompletedSuccessfully)
            {
                pending.AsTask().GetAwaiter().GetResult();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _runtime.Log.Warn("editor", $"Action '{action.Name}' failed", ex);
            Bell();
        }
    }

    private string? Finish()
    {
        var outcome = _outcome;
        _overlay = null;
        _anchor = null;
        var transient = outcome == Outcome.Accept && _runtime.Config.Current.Prompt.TransientPrompt;
        Renderer.Render(BuildFrame(live: false, transient, outcome == Outcome.Cancel ? "^C" : null));
        Renderer.Finish();
        return outcome switch
        {
            Outcome.Accept => _text,
            Outcome.Cancel => string.Empty,
            _ => null,
        };
    }

    /// <summary>
    /// If earlier output did not end with a newline, keep it and start below it (marked with an inverse '%', like zsh):
    /// the mark plus width-1 spaces wraps only when the cursor was not in column 0, then CR + erase cleans up.
    /// </summary>
    private void StartOnFreshLine()
    {
        var width = Math.Max(1, _runtime.Terminal.Width);
        var mark = Ansi.Style(Theme.Ui.Muted) + Ansi.Reverse + "%" + Ansi.Reset;
        _runtime.Terminal.Write(mark + new string(' ', width - 1) + "\r" + Ansi.ClearToEndOfLine);
    }

    private bool DrainRequests()
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

        if (changed && _overlay is { } overlay)
        {
            try
            {
                overlay.OnBufferChanged(this);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                OverlayFailed(overlay, ex);
            }
        }

        return changed;
    }

    // ───────────── Rendering ─────────────

    private void Render()
    {
        if (!_reading)
        {
            return;
        }

        var terminal = _runtime.Terminal;
        _renderedSize = (terminal.Width, terminal.Height);
        Renderer.Render(BuildFrame(live: true, transient: false, trailer: null));
        if (_runtime.Highlighter is SyntaxHighlighter highlighter)
        {
            _renderedHighlightVersion = highlighter.Version;
        }
    }

    private Frame BuildFrame(bool live, bool transient, string? trailer)
    {
        var theme = Theme;
        var builder = new FrameBuilder(_runtime.Terminal.Width);
        builder.WriteAnsi(transient ? TransientPrompt() : _prompt.Left);
        var inputRow = builder.Row;

        var overlay = live ? _overlay : null;
        var lineOverride = overlay is null ? null : OverlayInputLine(overlay);
        if (lineOverride is not null)
        {
            builder.WriteAnsi(lineOverride);
            builder.MarkCursor();
        }
        else
        {
            WriteInput(builder, theme, markCursor: live);
            if (live && overlay is null && Suggestion is { } suggestion)
            {
                WriteMultiline(builder, suggestion[_text.Length..], Ansi.Style(theme.Syntax.Suggestion));
            }

            if (trailer is not null)
            {
                builder.Write(trailer, Ansi.Style(theme.Ui.Muted));
            }
        }

        if (!transient && lineOverride is null && !string.IsNullOrEmpty(_prompt.Right))
        {
            builder.TryPlaceRight(inputRow, _prompt.Right);
        }

        if (overlay is not null)
        {
            var maxRows = Math.Max(1, _runtime.Terminal.Height - (builder.Row + 1));
            var lines = OverlayRender(overlay, builder.Width, maxRows);
            foreach (var line in lines.Take(maxRows))
            {
                builder.NewLine();
                builder.WriteAnsi(line, clip: true);
            }
        }

        return builder.Build();
    }

    private void WriteInput(FrameBuilder builder, Theme theme, bool markCursor)
    {
        var styles = ComputeStyles(theme);
        var i = 0;
        while (i < _text.Length)
        {
            if (markCursor && i == _cursor)
            {
                builder.MarkCursor();
            }

            if (_text[i] == '\n')
            {
                builder.NewLine();
                builder.WriteAnsi(_prompt.Continuation);
                i++;
                continue;
            }

            var next = TextNavigation.NextGrapheme(_text, i);
            builder.Write(_text[i..next], styles[i]);
            i = next;
        }

        if (markCursor && _cursor >= _text.Length)
        {
            builder.MarkCursor();
        }
    }

    private void WriteMultiline(FrameBuilder builder, string text, string style)
    {
        var lines = text.Split('\n');
        for (var l = 0; l < lines.Length; l++)
        {
            if (l > 0)
            {
                builder.NewLine();
                builder.WriteAnsi(_prompt.Continuation);
            }

            builder.Write(lines[l], style);
        }
    }

    private string[] ComputeStyles(Theme theme)
    {
        var styles = new string[_text.Length];
        Array.Fill(styles, Ansi.Style(theme.Syntax.Default));
        if (Settings.SyntaxHighlighting && _text.Length > 0)
        {
            try
            {
                foreach (var span in _runtime.Highlighter.Highlight(_text))
                {
                    var end = Math.Min(styles.Length, span.Start + span.Length);
                    for (var k = Math.Max(0, span.Start); k < end; k++)
                    {
                        styles[k] = span.Style;
                    }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _runtime.Log.Debug("editor", $"highlighting failed: {ex.Message}");
            }
        }

        if (Selection is { } selection)
        {
            var background = Ansi.Style(background: theme.Syntax.SelectionBackground);
            if (background.Length == 0)
            {
                background = Ansi.Reverse;
            }

            for (var k = selection.Start; k < selection.End; k++)
            {
                styles[k] += background;
            }
        }

        return styles;
    }

    private string TransientPrompt()
    {
        try
        {
            return _runtime.Prompt.RenderTransient(_promptContext ?? _runtime.CreatePromptContext());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _runtime.Log.Warn("editor", "transient prompt failed", ex);
            return _prompt.Left;
        }
    }

    private void UpdateSuggestion()
    {
        if (!_reading || !Settings.Autosuggestions || _text.Length == 0 || _cursor != _text.Length || _anchor is not null)
        {
            _suggestion = null;
            _suggestionFor = null;
            return;
        }

        if (string.Equals(_suggestionFor, _text, StringComparison.Ordinal))
        {
            return;
        }

        _suggestionFor = _text;
        _suggestion = null;
        try
        {
            var suggestion = _runtime.Autosuggest.Suggest(_text, _runtime.Engine.CurrentDirectory);
            if (suggestion is not null && suggestion.Length > _text.Length && suggestion.StartsWith(_text, StringComparison.OrdinalIgnoreCase))
            {
                _suggestion = suggestion.Replace("\r\n", "\n", StringComparison.Ordinal);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _runtime.Log.Debug("editor", $"autosuggest failed: {ex.Message}");
        }
    }

    // ───────────── Overlay safety ─────────────

    private OverlayKeyResult OverlayHandleKey(IEditorOverlay overlay, ConsoleKeyInfo key)
    {
        try
        {
            return overlay.HandleKey(key, this);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            OverlayFailed(overlay, ex);
            return OverlayKeyResult.NotHandled;
        }
    }

    private IReadOnlyList<string> OverlayRender(IEditorOverlay overlay, int width, int maxRows)
    {
        try
        {
            return overlay.Render(width, maxRows);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            OverlayFailed(overlay, ex);
            return [];
        }
    }

    private string? OverlayInputLine(IEditorOverlay overlay)
    {
        try
        {
            return overlay.InputLineOverride;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            OverlayFailed(overlay, ex);
            return null;
        }
    }

    private void OverlayFailed(IEditorOverlay overlay, Exception ex)
    {
        _runtime.Log.Warn("editor", $"Overlay {overlay.GetType().Name} failed; closing it", ex);
        if (ReferenceEquals(_overlay, overlay))
        {
            _overlay = null;
        }
    }

    // ───────────── IEditorBuffer ─────────────

    public void Insert(string text) => InsertText(NormalizeNewlines(text), EditKind.Other);

    public void Replace(string text, int cursor)
    {
        text = NormalizeNewlines(text);
        _undo.Checkpoint(_text, _cursor, EditKind.Other);
        _text = text;
        _cursor = Math.Clamp(cursor, 0, text.Length);
        _anchor = null;
        _preferredColumn = null;
        _undo.AfterEdit(_cursor);
    }

    public void Accept() => _outcome = Outcome.Accept;

    public void Redraw()
    {
        if (_reading)
        {
            Renderer.Invalidate();
            Render();
        }
    }

    public void OpenOverlay(IEditorOverlay overlay) => _overlay = overlay;

    public void CloseOverlay() => _overlay = null;

    public void ShowPanel(string panelId, string? argument = null) =>
        ShowPanelCore(panelId, host => host.Show(panelId, argument, _text));

    /// <summary>Opens panels queued by <see cref="IPickleShell.OpenPanelWhenIdle"/> while a pipeline was running.</summary>
    private void DrainPendingPanels()
    {
        while (_outcome == Outcome.None && _runtime.Engine.PendingPanels.TryDequeue(out var pending))
        {
            ShowPanelCore(pending.Panel.Id, host => host.Show(pending.Panel, pending.Argument, pending.CurrentInput ?? _text));
        }
    }

    private void ShowPanelCore(string panelId, Func<IPanelHost, PanelResult?> show)
    {
        var host = _runtime.ServiceRegistry.Get<IPanelHost>();
        if (host is null)
        {
            _runtime.Log.Debug("editor", $"No panel host; ignoring panel '{panelId}'");
            return;
        }

        if (!_reading)
        {
            ApplyPanelResult(this, show(host));
            return;
        }

        // Clear our frame so the panel (alternate screen) returns to a clean spot, then draw a fresh frame there.
        _overlay = null;
        Renderer.Invalidate();
        Renderer.Render(new Frame([[]], 0, 0, _runtime.Terminal.Width));
        Renderer.Reset();

        PanelResult? result = null;
        try
        {
            result = show(host);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _runtime.Log.Error("editor", $"Panel '{panelId}' failed", ex);
        }
        finally
        {
            _runtime.Terminal.SetEditMode(true);
        }

        ApplyPanelResult(this, result);
        StartOnFreshLine();
        Renderer.Reset();
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
                // SingleQuote also escapes the typographic quotes (‘ ’ ‚ ‛) PowerShell treats as single quotes.
                var cmd = "Set-Location -LiteralPath " + Translation.PowerShellText.SingleQuote(result.Text);
                buffer.Replace(cmd, cmd.Length);
                buffer.Accept();
                break;
        }
    }

    // ───────────── Editing primitives ─────────────

    private void Edit(EditKind kind, int start, int length, string insert, bool startsNewWord = false)
    {
        _undo.Checkpoint(_text, _cursor, kind, startsNewWord);
        _text = string.Concat(_text.AsSpan(0, start), insert, _text.AsSpan(start + length));
        _cursor = start + insert.Length;
        _anchor = null;
        _preferredColumn = null;
        _undo.AfterEdit(_cursor);
    }

    private void InsertText(string text, EditKind kind, bool startsNewWord = false)
    {
        var (start, end) = Selection ?? (_cursor, _cursor);
        Edit(kind, start, end - start, text, startsNewWord);
    }

    private void SelfInsert(char c)
    {
        if (char.IsHighSurrogate(c))
        {
            _pendingHighSurrogate = c;
            return;
        }

        string text;
        if (char.IsLowSurrogate(c))
        {
            if (_pendingHighSurrogate is not { } high)
            {
                return;
            }

            text = string.Concat(high, c);
        }
        else
        {
            text = c.ToString();
        }

        _pendingHighSurrogate = null;
        var newWord = !_inBurst && char.IsWhiteSpace(c) && _cursor > 0 && !char.IsWhiteSpace(_text[_cursor - 1]);
        InsertText(text, _inBurst ? EditKind.Paste : EditKind.Typing, newWord);
    }

    private void InsertLiteral(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Enter:
                InsertText("\n", EditKind.Paste);
                break;
            case ConsoleKey.Tab:
                InsertText("\t", EditKind.Paste);
                break;
            default:
                SelfInsert(key.KeyChar);
                break;
        }
    }

    private void MoveCursor(int position)
    {
        _cursor = Math.Clamp(position, 0, _text.Length);
        _anchor = null;
        _preferredColumn = null;
        _undo.BreakSequence();
    }

    private void Bell()
    {
        switch (Settings.BellStyle?.Trim().ToLowerInvariant())
        {
            case "audible":
                _runtime.Terminal.Write("\u0007");
                break;
            case "visual":
                _runtime.Terminal.Write("\u001b[?5h");
                _runtime.Terminal.Flush();
                Thread.Sleep(60);
                _runtime.Terminal.Write("\u001b[?5l");
                break;
        }
    }

    internal static bool IsIncompleteInput(string text)
    {
        Parser.ParseInput(text, out _, out var errors);
        return errors.Length > 0 && errors.All(e => e.IncompleteInput);
    }

    private static bool IsPrintable(ConsoleKeyInfo key)
    {
        var c = key.KeyChar;
        if (c == '\0' || char.IsControl(c))
        {
            return false;
        }

        // Ctrl or Alt alone makes a shortcut; both together is AltGr, which types characters on many layouts.
        return key.Modifiers.HasFlag(ConsoleModifiers.Control) == key.Modifiers.HasFlag(ConsoleModifiers.Alt);
    }

    private static bool IsLiteralPasteKey(ConsoleKeyInfo key) =>
        (key.Key is ConsoleKey.Enter or ConsoleKey.Tab && key.Modifiers == 0) || IsPrintable(key);

    private static string NormalizeNewlines(string text) =>
        text.Contains('\r', StringComparison.Ordinal) ? text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n') : text;

    // ───────────── ReadSimpleLine ─────────────

    public string? ReadSimpleLine(string prompt, bool mask)
    {
        var terminal = _runtime.Terminal;
        var nested = _reading;
        if (!nested)
        {
            terminal.SetEditMode(true);
        }

        var renderer = new FrameRenderer(terminal);
        var indent = Math.Clamp(terminal.OutputColumn, 0, Math.Max(0, terminal.Width - 1));
        renderer.Reset(indent);
        var text = new StringBuilder();
        var cursor = 0;
        char? high = null;
        try
        {
            while (true)
            {
                renderer.Render(BuildSimpleFrame(prompt, text.ToString(), cursor, mask, indent, trailer: null));
                var key = terminal.ReadKey();
                var chord = KeyChord.FromKeyInfo(key).ToString();
                switch (chord)
                {
                    case "Enter":
                        renderer.Render(BuildSimpleFrame(prompt, text.ToString(), text.Length, mask, indent, trailer: null));
                        renderer.Finish();
                        return text.ToString();
                    case "Ctrl+C":
                        renderer.Render(BuildSimpleFrame(prompt, text.ToString(), text.Length, mask, indent, trailer: "^C"));
                        renderer.Finish();
                        _runtime.Engine.StopCurrent();
                        return null;
                    case "Ctrl+D" when text.Length == 0:
                        renderer.Finish();
                        return null;
                    case "Backspace" when cursor > 0:
                        var previous = TextNavigation.PreviousGrapheme(text.ToString(), cursor);
                        text.Remove(previous, cursor - previous);
                        cursor = previous;
                        break;
                    case "Delete" when cursor < text.Length:
                        text.Remove(cursor, TextNavigation.NextGrapheme(text.ToString(), cursor) - cursor);
                        break;
                    case "LeftArrow":
                        cursor = TextNavigation.PreviousGrapheme(text.ToString(), cursor);
                        break;
                    case "RightArrow":
                        cursor = TextNavigation.NextGrapheme(text.ToString(), cursor);
                        break;
                    case "Home" or "Ctrl+A":
                        cursor = 0;
                        break;
                    case "End" or "Ctrl+E":
                        cursor = text.Length;
                        break;
                    case "Escape":
                        text.Clear();
                        cursor = 0;
                        break;
                    case "Ctrl+U":
                        text.Remove(0, cursor);
                        cursor = 0;
                        break;
                    default:
                        if (char.IsHighSurrogate(key.KeyChar))
                        {
                            high = key.KeyChar;
                        }
                        else if (IsPrintable(key))
                        {
                            var insert = char.IsLowSurrogate(key.KeyChar) && high is { } h ? string.Concat(h, key.KeyChar) : key.KeyChar.ToString();
                            high = null;
                            if (!char.IsSurrogate(insert[0]) || insert.Length == 2)
                            {
                                text.Insert(cursor, insert);
                                cursor += insert.Length;
                            }
                        }

                        break;
                }
            }
        }
        finally
        {
            if (!nested)
            {
                terminal.SetEditMode(false);
            }
        }
    }

    private Frame BuildSimpleFrame(string prompt, string text, int cursor, bool mask, int indent, string? trailer)
    {
        var builder = new FrameBuilder(_runtime.Terminal.Width, indent);
        builder.WriteAnsi(prompt);
        var i = 0;
        while (i < text.Length)
        {
            if (i == cursor)
            {
                builder.MarkCursor();
            }

            var next = TextNavigation.NextGrapheme(text, i);
            builder.Write(mask ? "•" : text[i..next], string.Empty);
            i = next;
        }

        if (cursor >= text.Length)
        {
            builder.MarkCursor();
        }

        if (trailer is not null)
        {
            builder.Write(trailer, Ansi.Style(Theme.Ui.Muted));
        }

        return builder.Build();
    }
}
