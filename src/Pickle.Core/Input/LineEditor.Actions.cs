using Pickle.Abstractions;

namespace Pickle.Core.Input;

// The built-in editing actions (bound in DefaultKeyBindings.Table).
public sealed partial class LineEditor
{
    private void RegisterActions()
    {
        void Register(string name, string description, Action action) =>
            _runtime.KeyBindingRegistry.RegisterAction(name, description, (_, _) =>
            {
                action();
                return ValueTask.CompletedTask;
            });

        Register(EditorActionNames.AcceptLine, "Run the command (inserts a newline while the input is incomplete)", AcceptLineAction);
        Register(EditorActionNames.InsertNewline, "Insert a newline", () => InsertText("\n", EditKind.Other));
        Register(EditorActionNames.CancelLine, "Copy the selection, or cancel the current line", CancelLineAction);
        Register(EditorActionNames.ClearLine, "Clear the selection or the whole input", ClearLineAction);
        Register(EditorActionNames.BackwardChar, "Move left", BackwardChar);
        Register(EditorActionNames.ForwardChar, "Move right (accepts the suggestion at the end)", ForwardChar);
        Register(EditorActionNames.BackwardWord, "Move to the previous word", () => MoveOrBell(TextNavigation.WordStartBefore(_text, _cursor)));
        Register(EditorActionNames.ForwardWord, "Move to the next word (accepts a suggested word at the end)", ForwardWord);
        Register(EditorActionNames.BeginningOfLine, "Move to the start of the line, then of the input", () => MoveOrBell(HomeTarget()));
        Register(EditorActionNames.EndOfLine, "Move to the end of the line, then of the input (accepts the suggestion)", EndOfLine);
        Register(EditorActionNames.BackwardDeleteChar, "Delete the previous character", BackwardDeleteChar);
        Register(EditorActionNames.DeleteChar, "Delete the next character", DeleteChar);
        Register(EditorActionNames.BackwardKillWord, "Delete the previous word", () => DeleteRange(TextNavigation.WordStartBefore(_text, _cursor), _cursor));
        Register(EditorActionNames.KillWord, "Delete the next word", () => DeleteRange(_cursor, TextNavigation.WordEndAfter(_text, _cursor)));
        Register(EditorActionNames.KillToEnd, "Delete to the end of the line", KillToEnd);
        Register(EditorActionNames.Undo, "Undo", () => UndoRedo(redo: false));
        Register(EditorActionNames.Redo, "Redo", () => UndoRedo(redo: true));
        Register(EditorActionNames.SelectBackwardChar, "Extend the selection left", () => SelectTo(TextNavigation.PreviousGrapheme(_text, _cursor)));
        Register(EditorActionNames.SelectForwardChar, "Extend the selection right", () => SelectTo(TextNavigation.NextGrapheme(_text, _cursor)));
        Register(EditorActionNames.SelectBackwardWord, "Extend the selection to the previous word", () => SelectTo(TextNavigation.WordStartBefore(_text, _cursor)));
        Register(EditorActionNames.SelectForwardWord, "Extend the selection to the next word", () => SelectTo(TextNavigation.WordEndAfter(_text, _cursor)));
        Register(EditorActionNames.SelectToStart, "Extend the selection to the start of the line", () => SelectTo(HomeTarget()));
        Register(EditorActionNames.SelectToEnd, "Extend the selection to the end of the line", () => SelectTo(EndTarget()));
        Register(EditorActionNames.SelectAll, "Select the whole input", SelectAll);
        Register(EditorActionNames.Copy, "Copy the selection (or the whole input)", Copy);
        Register(EditorActionNames.Cut, "Cut the selection", Cut);
        Register(EditorActionNames.Paste, "Paste from the clipboard", Paste);
        Register(EditorActionNames.HistoryPrevious, "Previous line, or previous history entry matching the typed prefix", HistoryPrevious);
        Register(EditorActionNames.HistoryNext, "Next line, or next history entry matching the typed prefix", HistoryNext);
        Register(EditorActionNames.AcceptSuggestion, "Accept the autosuggestion", AcceptSuggestion);
        Register(EditorActionNames.AcceptSuggestionWord, "Accept the next word of the autosuggestion", AcceptSuggestionWord);
        Register(EditorActionNames.ClearScreen, "Clear the screen", ClearScreen);
        Register(EditorActionNames.ExitIfEmpty, "Exit when the input is empty, otherwise delete the next character", ExitIfEmpty);
    }

    private void AcceptLineAction()
    {
        // The Enter that ends a pasted burst is dropped so pasted commands never run before the user looks at them.
        if (_inBurst)
        {
            return;
        }

        if (_text.Length > 0 && IsIncompleteInput(_text))
        {
            InsertText("\n", EditKind.Other);
            return;
        }

        Accept();
    }

    private void CancelLineAction()
    {
        if (Selection is not null)
        {
            Copy();
            _anchor = null;
            return;
        }

        _outcome = Outcome.Cancel;
    }

    private void ClearLineAction()
    {
        if (_anchor is not null)
        {
            _anchor = null;
            return;
        }

        if (_text.Length > 0)
        {
            Edit(EditKind.Other, 0, _text.Length, string.Empty);
        }
    }

    private void BackwardChar()
    {
        if (Selection is { } selection)
        {
            MoveCursor(selection.Start);
            return;
        }

        MoveOrBell(TextNavigation.PreviousGrapheme(_text, _cursor));
    }

    private void ForwardChar()
    {
        if (Selection is { } selection)
        {
            MoveCursor(selection.End);
            return;
        }

        if (_cursor == _text.Length && Suggestion is not null)
        {
            AcceptSuggestion();
            return;
        }

        MoveOrBell(TextNavigation.NextGrapheme(_text, _cursor));
    }

    private void ForwardWord()
    {
        if (_cursor == _text.Length && _anchor is null && Suggestion is not null)
        {
            AcceptSuggestionWord();
            return;
        }

        MoveOrBell(TextNavigation.WordEndAfter(_text, _cursor));
    }

    private void EndOfLine()
    {
        if (_cursor == _text.Length && _anchor is null && Suggestion is not null)
        {
            AcceptSuggestion();
            return;
        }

        MoveOrBell(EndTarget());
    }

    private int HomeTarget()
    {
        var start = TextNavigation.LineStart(_text, _cursor);
        return start == _cursor ? 0 : start;
    }

    private int EndTarget()
    {
        var end = TextNavigation.LineEnd(_text, _cursor);
        return end == _cursor ? _text.Length : end;
    }

    private void MoveOrBell(int position)
    {
        if (position == _cursor && _anchor is null)
        {
            Bell();
            return;
        }

        MoveCursor(position);
    }

    private void BackwardDeleteChar()
    {
        if (Selection is not null)
        {
            DeleteSelection();
            return;
        }

        if (_cursor == 0)
        {
            Bell();
            return;
        }

        var start = TextNavigation.PreviousGrapheme(_text, _cursor);
        Edit(EditKind.Deleting, start, _cursor - start, string.Empty);
    }

    private void DeleteChar()
    {
        if (Selection is not null)
        {
            DeleteSelection();
            return;
        }

        if (_cursor >= _text.Length)
        {
            Bell();
            return;
        }

        Edit(EditKind.Deleting, _cursor, TextNavigation.NextGrapheme(_text, _cursor) - _cursor, string.Empty);
    }

    private void KillToEnd()
    {
        if (Selection is not null)
        {
            DeleteSelection();
            return;
        }

        var end = TextNavigation.LineEnd(_text, _cursor);
        DeleteRange(_cursor, end == _cursor ? Math.Min(_text.Length, end + 1) : end);
    }

    private void DeleteRange(int start, int end)
    {
        if (Selection is not null)
        {
            DeleteSelection();
            return;
        }

        if (end <= start)
        {
            Bell();
            return;
        }

        Edit(EditKind.Other, start, end - start, string.Empty);
    }

    private void DeleteSelection()
    {
        if (Selection is { } selection)
        {
            Edit(EditKind.Other, selection.Start, selection.End - selection.Start, string.Empty);
        }
    }

    private void UndoRedo(bool redo)
    {
        var ok = redo ? _undo.TryRedo(_text, _cursor, out var state) : _undo.TryUndo(_text, _cursor, out state);
        if (!ok)
        {
            Bell();
            return;
        }

        _text = state.Text;
        _cursor = Math.Clamp(state.Cursor, 0, _text.Length);
        _anchor = null;
        _preferredColumn = null;
    }

    private void SelectTo(int position)
    {
        _anchor ??= _cursor;
        _cursor = Math.Clamp(position, 0, _text.Length);
        _preferredColumn = null;
        _undo.BreakSequence();
    }

    private void SelectAll()
    {
        if (_text.Length == 0)
        {
            Bell();
            return;
        }

        _anchor = 0;
        _cursor = _text.Length;
        _undo.BreakSequence();
    }

    private void Copy()
    {
        var text = Selection is { } selection ? _text[selection.Start..selection.End] : _text;
        if (text.Length == 0)
        {
            Bell();
            return;
        }

        ClipboardService.SetText(text);
    }

    private void Cut()
    {
        if (Selection is not { } selection)
        {
            Bell();
            return;
        }

        ClipboardService.SetText(_text[selection.Start..selection.End]);
        DeleteSelection();
    }

    private void Paste()
    {
        var text = ClipboardService.GetText();
        if (string.IsNullOrEmpty(text))
        {
            Bell();
            return;
        }

        InsertText(NormalizeNewlines(text), EditKind.Other);
    }

    private void HistoryPrevious()
    {
        if (_history is null && _anchor is null && TextNavigation.LineIndex(_text, _cursor) > 0)
        {
            MoveLine(up: true);
            return;
        }

        var navigator = _history ?? new HistoryNavigator(_text, _runtime.History.Entries);
        if (navigator.Previous() is not { } entry)
        {
            Bell();
            _history = _history is null ? null : navigator;
            _keepHistory = _history is not null;
            return;
        }

        _history = navigator;
        _keepHistory = true;
        SetText(entry, EditKind.History);
    }

    private void HistoryNext()
    {
        if (_history is null)
        {
            if (_anchor is null && TextNavigation.LineIndex(_text, _cursor) < TextNavigation.LineCount(_text) - 1)
            {
                MoveLine(up: false);
            }
            else
            {
                Bell();
            }

            return;
        }

        _keepHistory = true;
        if (_history.Next() is not { } entry)
        {
            Bell();
            return;
        }

        SetText(entry, EditKind.History);
    }

    private void SetText(string text, EditKind kind)
    {
        text = NormalizeNewlines(text);
        _undo.Checkpoint(_text, _cursor, kind);
        _text = text;
        _cursor = text.Length;
        _anchor = null;
        _preferredColumn = null;
        _undo.AfterEdit(_cursor);
    }

    private void MoveLine(bool up)
    {
        var column = _preferredColumn ?? TextNavigation.ColumnOf(_text, _cursor);
        var lineStart = TextNavigation.LineStart(_text, _cursor);
        var targetStart = up
            ? TextNavigation.LineStart(_text, lineStart - 1)
            : TextNavigation.LineEnd(_text, _cursor) + 1;
        MoveCursor(TextNavigation.IndexAtColumn(_text, targetStart, column));
        _preferredColumn = column;
    }

    private void AcceptSuggestion()
    {
        if (Suggestion is not { } suggestion)
        {
            MoveOrBell(_text.Length);
            return;
        }

        SetText(suggestion, EditKind.Other);
    }

    private void AcceptSuggestionWord()
    {
        if (_cursor != _text.Length || Suggestion is not { } suggestion)
        {
            MoveOrBell(TextNavigation.WordEndAfter(_text, _cursor));
            return;
        }

        SetText(suggestion[..TextNavigation.WordEndAfter(suggestion, _text.Length)], EditKind.Other);
    }

    private void ClearScreen()
    {
        _runtime.Terminal.Write("\u001b[H\u001b[2J");
        Renderer.Reset();
    }

    private void ExitIfEmpty()
    {
        if (_text.Length == 0)
        {
            _outcome = Outcome.EndOfFile;
            return;
        }

        DeleteChar();
    }
}
