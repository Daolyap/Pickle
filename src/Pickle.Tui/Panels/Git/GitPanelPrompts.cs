namespace Pickle.Tui.Panels.Git;

/// <summary>The modal questions the Git panel asks; tests substitute an implementation that answers instantly.</summary>
internal interface IGitPanelPrompts
{
    bool Confirm(string title, string message);

    void Info(string title, string message);

    void Error(string message);

    /// <summary>Single-line text input; null when cancelled.</summary>
    string? AskText(string title, string label, string initial = "");

    /// <summary>Commit message and amend flag; null when cancelled.</summary>
    (string Message, bool Amend)? AskCommit();

    /// <summary>Stash message (may be empty) and whether to include untracked files; null when cancelled.</summary>
    (string Message, bool IncludeUntracked)? AskStash();
}
