namespace Pickle.Core.Terminal;

/// <summary>
/// The console as seen by the line editor, prompt and host UI. Real: <see cref="ConsoleTerminal"/>.
/// Tests: <c>Pickle.Testing.VirtualTerminal</c> (an ANSI interpreter with a cell grid you can snapshot).
/// All output is ANSI/VT text; never call System.Console directly from editor/render code.
/// </summary>
public interface ITerminal
{
    int Width { get; }

    int Height { get; }

    /// <summary>stdin and stdout are attached to a terminal (not redirected).</summary>
    bool IsInteractive { get; }

    /// <summary>ANSI/VT sequences are rendered (false when stdout is redirected to a file/pipe).</summary>
    bool SupportsAnsi { get; }

    bool KeyAvailable { get; }

    /// <summary>Blocking read of one key, without echo.</summary>
    ConsoleKeyInfo ReadKey(CancellationToken cancellationToken = default);

    void Write(string text);

    void Flush();

    /// <summary>
    /// Called before the line editor starts reading (Ctrl+C becomes a key) and after it stops
    /// (Ctrl+C becomes SIGINT/CTRL_C_EVENT again so running commands can be interrupted).
    /// </summary>
    void SetEditMode(bool editing);

    /// <summary>0-based cursor position. May be slow (queries the terminal); avoid in hot paths.</summary>
    (int Column, int Row) GetCursorPosition();

    string Title { get; set; }

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for input. Returns false on timeout. Terminals that cannot wait return
    /// true, meaning "call <see cref="ReadKey"/>" (which then blocks).
    /// </summary>
    bool WaitForInput(TimeSpan timeout, CancellationToken cancellationToken = default) => true;

    /// <summary>Best-effort 0-based column where the next output lands, without a round trip to the terminal.</summary>
    int OutputColumn => GetCursorPosition().Column;
}
