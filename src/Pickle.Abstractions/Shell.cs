using System.Management.Automation;

namespace Pickle.Abstractions;

public enum ShellTarget
{
    /// <summary>The interactive runspace (sees the user's variables, jobs, location). Serialized with the REPL.</summary>
    Main,

    /// <summary>A background runspace pool with the same initial session state. Use for slow work (winget, WUA).</summary>
    Background,
}

public sealed record ShellResult(IReadOnlyList<PSObject> Output, IReadOnlyList<ErrorRecord> Errors)
{
    public bool HadErrors => Errors.Count > 0;

    public static ShellResult Empty { get; } = new([], []);
}

/// <summary>Bridge between plugins/panels and the running shell.</summary>
public interface IPickleShell
{
    string CurrentDirectory { get; }

    bool IsInteractive { get; }

    /// <summary>
    /// The caller is inside a pipeline that holds the main runspace (e.g. a `pk` command, typed or run by a key
    /// handler): work that needs the runspace from another thread can't run until it returns.
    /// </summary>
    bool IsBusy { get; }

    Task<ShellResult> InvokeAsync(
        string script,
        IReadOnlyDictionary<string, object?>? parameters = null,
        ShellTarget target = ShellTarget.Main,
        CancellationToken cancellationToken = default);

    /// <summary>Insert text at the line editor cursor (applied when the editor next renders, e.g. after a panel closes).</summary>
    void InsertText(string text);

    /// <summary>Replace the whole input buffer.</summary>
    void ReplaceInput(string text);

    /// <summary>Queue a command to run in the main runspace as if the user typed it and pressed Enter.</summary>
    void SubmitCommand(string commandLine);

    /// <summary>Change the main runspace location (applied immediately if idle, otherwise queued).</summary>
    void SetLocation(string path);

    /// <summary>Write a line to the terminal scrollback (ANSI allowed). Safe to call from any thread while no panel is open.</summary>
    void WriteLine(string text);

    /// <summary>
    /// Open <paramref name="panel"/> as soon as the prompt is back (panels run on the REPL thread, never inside a
    /// running pipeline). Its result is applied to the input line as if it had been opened with a key.
    /// <paramref name="currentInput"/> is what the panel sees as the input line (default: the new prompt's text).
    /// </summary>
    void OpenPanelWhenIdle(PanelDescriptor panel, string? argument = null, string? currentInput = null);
}
