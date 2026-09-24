using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Pickle.Core.Terminal;

namespace Pickle.Core.Input;

/// <summary>System clipboard as seen by the line editor. Register an instance in the service registry to override.</summary>
public interface IClipboard
{
    void SetText(string text);

    string? GetText();
}

public static class Clipboard
{
    /// <summary>Win32 clipboard on a real Windows console; elsewhere OSC 52 for copy plus an in-process buffer for paste.</summary>
    public static IClipboard Create(PickleRuntime runtime)
    {
        if (runtime.ServiceRegistry.Get<IClipboard>() is { } registered)
        {
            return registered;
        }

        if (OperatingSystem.IsWindows() && runtime.Terminal is ConsoleTerminal)
        {
            return new WindowsClipboard(new TerminalClipboard(runtime.Terminal, emitOsc52: false));
        }

        return new TerminalClipboard(runtime.Terminal, emitOsc52: runtime.Terminal is ConsoleTerminal { SupportsAnsi: true });
    }
}

/// <summary>
/// Copies with OSC 52 (the terminal puts it on the system clipboard, also over SSH) and remembers the text so paste
/// works in-process; terminals don't let applications read the clipboard back.
/// </summary>
public sealed class TerminalClipboard(ITerminal terminal, bool emitOsc52 = true) : IClipboard
{
    private string? _text;

    public void SetText(string text)
    {
        _text = text;
        if (emitOsc52)
        {
            terminal.Write("\u001b]52;c;" + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) + "\u0007");
            terminal.Flush();
        }
    }

    public string? GetText() => _text;
}

[SupportedOSPlatform("windows")]
internal sealed partial class WindowsClipboard(IClipboard fallback) : IClipboard
{
    private const uint UnicodeText = 13;
    private const uint GmemMoveable = 0x0002;

    public void SetText(string text)
    {
        fallback.SetText(text);
        if (!Open())
        {
            return;
        }

        try
        {
            EmptyClipboard();
            var bytes = (nuint)((text.Length + 1) * 2);
            var handle = GlobalAlloc(GmemMoveable, bytes);
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var target = GlobalLock(handle);
            if (target == IntPtr.Zero)
            {
                GlobalFree(handle);
                return;
            }

            try
            {
                Marshal.Copy((text + "\0").ToCharArray(), 0, target, text.Length + 1);
            }
            finally
            {
                GlobalUnlock(handle);
            }

            if (SetClipboardData(UnicodeText, handle) == IntPtr.Zero)
            {
                GlobalFree(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    public string? GetText()
    {
        if (!IsClipboardFormatAvailable(UnicodeText) || !Open())
        {
            return fallback.GetText();
        }

        try
        {
            var handle = GetClipboardData(UnicodeText);
            if (handle == IntPtr.Zero)
            {
                return fallback.GetText();
            }

            var source = GlobalLock(handle);
            if (source == IntPtr.Zero)
            {
                return fallback.GetText();
            }

            try
            {
                return Marshal.PtrToStringUni(source);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    // Another process may hold the clipboard for a moment.
    private static bool Open()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return false;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(IntPtr hWndNewOwner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsClipboardFormatAvailable(uint format);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr GetClipboardData(uint uFormat);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GlobalAlloc(uint uFlags, nuint dwBytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GlobalLock(IntPtr hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(IntPtr hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GlobalFree(IntPtr hMem);
}
