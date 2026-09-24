namespace Pickle.Core.Input;

internal enum EditKind
{
    None,
    Typing,
    Deleting,
    Paste,
    History,
    Other,
}

/// <summary>
/// Snapshot undo/redo. Consecutive edits of the same kind (typing a word, a run of deletes, one paste burst,
/// stepping through history) coalesce into one undo step.
/// </summary>
internal sealed class UndoStack
{
    private readonly Stack<(string Text, int Cursor)> _undo = new();
    private readonly Stack<(string Text, int Cursor)> _redo = new();
    private EditKind _lastKind;
    private int _lastCursor = -1;

    public int Count => _undo.Count;

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        BreakSequence();
    }

    /// <summary>Cursor movement and other non-edits end the current coalescing run.</summary>
    public void BreakSequence()
    {
        _lastKind = EditKind.None;
        _lastCursor = -1;
    }

    /// <summary>Record the state before an edit.</summary>
    public void Checkpoint(string text, int cursor, EditKind kind, bool startsNewWord = false)
    {
        var coalesce = kind is EditKind.Typing or EditKind.Deleting or EditKind.Paste or EditKind.History
            && kind == _lastKind
            && (kind == EditKind.History || cursor == _lastCursor)
            && !startsNewWord;
        if (!coalesce)
        {
            _undo.Push((text, cursor));
        }

        _redo.Clear();
        _lastKind = kind;
    }

    public void AfterEdit(int cursor) => _lastCursor = cursor;

    public bool TryUndo(string text, int cursor, out (string Text, int Cursor) state) => Swap(_undo, _redo, text, cursor, out state);

    public bool TryRedo(string text, int cursor, out (string Text, int Cursor) state) => Swap(_redo, _undo, text, cursor, out state);

    private bool Swap(Stack<(string, int)> from, Stack<(string, int)> to, string text, int cursor, out (string Text, int Cursor) state)
    {
        BreakSequence();
        if (!from.TryPop(out state))
        {
            return false;
        }

        to.Push((text, cursor));
        return true;
    }
}
