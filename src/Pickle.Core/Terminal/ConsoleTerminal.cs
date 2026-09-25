using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Pickle.Core.Terminal;

/// <summary>
/// Real terminal backed by System.Console. Input uses Console.ReadKey (full key fidelity on Windows via
/// ReadConsoleInput; terminfo-decoded sequences on Unix — the same approach PSReadLine uses).
/// </summary>
public sealed class ConsoleTerminal : ITerminal
{
    private readonly TextWriter _out;
    private readonly bool _stripAnsi;
    private readonly WindowsConsole.Modes? _startupModes;
    private WindowsConsole.Modes? _beforeNativeProgram;
    private int _nativePrograms;
    private string _title = "Pickle";
    private int _outputColumn;

    public ConsoleTerminal()
    {
        if (OperatingSystem.IsWindows())
        {
            _startupModes = WindowsConsole.Capture();
            WindowsConsole.SetOutputMode(_startupModes, editing: false);
        }

        try
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

            // Windows reads keys through ReadConsoleInputW, so the input code page is irrelevant to Pickle; changing
            // it only alters what native programs (ssh, python's input()) see.
            if (!OperatingSystem.IsWindows() && !Console.IsInputRedirected)
            {
                Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            }
        }
        catch (IOException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }

        _out = Console.Out;

        // `pickle -c ... > file` should produce plain text, like pwsh. PICKLE_FORCE_COLOR=1 keeps ANSI.
        _stripAnsi = Console.IsOutputRedirected && Environment.GetEnvironmentVariable("PICKLE_FORCE_COLOR") is not ("1" or "true");
    }

    /// <summary>True when stdout is a terminal (or color is forced), i.e. ANSI sequences are rendered.</summary>
    public bool SupportsAnsi => !_stripAnsi;

    public int Width
    {
        get
        {
            try
            {
                var w = Console.WindowWidth;
                return w > 0 ? w : 120;
            }
            catch (IOException)
            {
                return 120;
            }
            catch (PlatformNotSupportedException)
            {
                return 120;
            }
        }
    }

    public int Height
    {
        get
        {
            try
            {
                var h = Console.WindowHeight;
                return h > 0 ? h : 40;
            }
            catch (IOException)
            {
                return 40;
            }
            catch (PlatformNotSupportedException)
            {
                return 40;
            }
        }
    }

    public bool IsInteractive => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    public bool KeyAvailable
    {
        get
        {
            try
            {
                return Console.KeyAvailable;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public ConsoleKeyInfo ReadKey(CancellationToken cancellationToken = default)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            return Console.ReadKey(intercept: true);
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            if (KeyAvailable)
            {
                return Console.ReadKey(intercept: true);
            }

            Thread.Sleep(10);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return default;
    }

    public bool WaitForInput(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (KeyAvailable)
            {
                return true;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }

            Thread.Sleep(10);
        }
    }

    public int OutputColumn
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    return Console.CursorLeft;
                }
                catch (IOException)
                {
                }
                catch (PlatformNotSupportedException)
                {
                }
            }

            // On Unix asking the terminal means a DSR round trip that hangs on terminals that never answer.
            return _outputColumn;
        }
    }

    public void Write(string text)
    {
        TrackColumn(text);
        _out.Write(_stripAnsi ? Abstractions.TextWidth.StripAnsi(text) : text);
    }

    private void TrackColumn(string text)
    {
        var lineStart = text.AsSpan().LastIndexOfAny('\n', '\r');
        var column = lineStart < 0 ? _outputColumn : 0;
        column += Abstractions.TextWidth.VisibleWidth(lineStart < 0 ? text : text[(lineStart + 1)..]);
        var width = Math.Max(1, Width);
        _outputColumn = column % width;
    }

    public void Flush() => _out.Flush();

    public void SetEditMode(bool editing)
    {
        try
        {
            Console.TreatControlCAsInput = editing;
        }
        catch (IOException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }

        if (OperatingSystem.IsWindows() && _startupModes is not null)
        {
            WindowsConsole.SetOutputMode(_startupModes, editing);
        }
    }

    public void BeginNativeProgram()
    {
        if (!OperatingSystem.IsWindows() || _startupModes is null || Interlocked.Increment(ref _nativePrograms) != 1)
        {
            return;
        }

        _beforeNativeProgram = WindowsConsole.Capture();
        WindowsConsole.Restore(_startupModes with { Output = null, Error = null });
        WindowsConsole.SetOutputMode(_startupModes, editing: false);
    }

    public void EndNativeProgram()
    {
        if (!OperatingSystem.IsWindows() || _startupModes is null || Interlocked.Decrement(ref _nativePrograms) != 0)
        {
            return;
        }

        if (_beforeNativeProgram is { } saved)
        {
            WindowsConsole.Restore(saved);
            _beforeNativeProgram = null;
        }
    }

    public (int Column, int Row) GetCursorPosition()
    {
        try
        {
            var (left, top) = Console.GetCursorPosition();
            return (left, top);
        }
        catch (IOException)
        {
            return (0, 0);
        }
        catch (PlatformNotSupportedException)
        {
            return (0, 0);
        }
    }

    public string Title
    {
        get => _title;
        set
        {
            _title = value;
            Write(Abstractions.Ansi.SetTitle(value));
        }
    }
}

/// <summary>
/// Console modes are shared by every process on the console, so Pickle keeps its changes to the minimum and hands
/// native programs the modes the console had at startup, as ConsoleHost does.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsConsole
{
    private const int StdInputHandle = -10;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    // Makes LF a pure line feed (no carriage return). The editor's renderer always writes CR LF itself and wants
    // VT-style deferred wrap at the right margin; everything else (PowerShell output, pk commands, native programs)
    // writes bare LF and would print as a staircase with it set.
    private const uint DisableNewlineAutoReturn = 0x0008;

    internal sealed record Modes(uint? Input, uint? Output, uint? Error);

    public static Modes Capture() => new(Get(StdInputHandle), Get(StdOutputHandle), Get(StdErrorHandle));

    public static void Restore(Modes modes)
    {
        Set(StdInputHandle, modes.Input);
        Set(StdOutputHandle, modes.Output);
        Set(StdErrorHandle, modes.Error);
    }

    public static void SetOutputMode(Modes startup, bool editing)
    {
        foreach (var (id, mode) in new[] { (StdOutputHandle, startup.Output), (StdErrorHandle, startup.Error) })
        {
            if (mode is { } m)
            {
                var wanted = m | EnableVirtualTerminalProcessing;
                Set(id, editing ? wanted | DisableNewlineAutoReturn : wanted & ~DisableNewlineAutoReturn);
            }
        }
    }

    private static uint? Get(int id)
    {
        var handle = GetStdHandle(id);
        return handle != IntPtr.Zero && handle != new IntPtr(-1) && GetConsoleMode(handle, out var mode) ? mode : null;
    }

    private static void Set(int id, uint? mode)
    {
        var handle = GetStdHandle(id);
        if (mode is { } m && handle != IntPtr.Zero && handle != new IntPtr(-1))
        {
            _ = SetConsoleMode(handle, m);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}
