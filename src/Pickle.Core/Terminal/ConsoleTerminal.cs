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
    private string _title = "Pickle";

    public ConsoleTerminal()
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsConsole.EnableVirtualTerminal();
        }

        try
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            if (!Console.IsInputRedirected)
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

    public void Write(string text) => _out.Write(_stripAnsi ? Abstractions.TextWidth.StripAnsi(text) : text);

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

[SupportedOSPlatform("windows")]
internal static partial class WindowsConsole
{
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private const uint EnableVirtualTerminalProcessing = 0x0004;
    private const uint DisableNewlineAutoReturn = 0x0008;

    public static void EnableVirtualTerminal()
    {
        foreach (var id in new[] { StdOutputHandle, StdErrorHandle })
        {
            var handle = GetStdHandle(id);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            {
                continue;
            }

            if (GetConsoleMode(handle, out var mode))
            {
                _ = SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing | DisableNewlineAutoReturn);
            }
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
