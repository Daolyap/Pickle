using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Pickle.Windows.Fonts;

/// <summary>Registered fonts (machine and current user), per-user font installation and the console window's font.</summary>
[SupportedOSPlatform("windows")]
public static partial class InstalledFonts
{
    private const string FontsKey = @"Software\Microsoft\Windows NT\CurrentVersion\Fonts";

    /// <summary>Value names under both Fonts keys, e.g. "Cascadia Code NF Regular (TrueType)".</summary>
    public static IReadOnlyList<string> RegisteredNames()
    {
        var names = new List<string>();
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var key = root.OpenSubKey(FontsKey);
                if (key is not null)
                {
                    names.AddRange(key.GetValueNames());
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }

        return names;
    }

    public static FontCatalog Catalog() => new(RegisteredNames());

    /// <summary>The per-user fonts folder (Windows 10 1809+ installs fonts there without administrator rights).</summary>
    public static string UserFontsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");

    /// <summary>
    /// Installs font files for the current user: copies them to <see cref="UserFontsDirectory"/>, registers them under
    /// HKCU, loads them into this session and tells running programs the font list changed. Returns the installed paths.
    /// </summary>
    public static IReadOnlyList<string> InstallForCurrentUser(IReadOnlyList<(string Source, FontFileSpec Spec)> files)
    {
        Directory.CreateDirectory(UserFontsDirectory);
        using var key = Registry.CurrentUser.CreateSubKey(FontsKey, writable: true);
        var installed = new List<string>();
        foreach (var (source, spec) in files)
        {
            var target = Path.Combine(UserFontsDirectory, spec.FileName);
            if (!File.Exists(target) || !SameContent(source, target))
            {
                // A font loaded into the session is locked; unload the old copy before replacing it.
                _ = RemoveFontResource(target);
                File.Copy(source, target, overwrite: true);
            }

            key.SetValue(spec.RegistryName, target, RegistryValueKind.String);
            _ = AddFontResource(target);
            installed.Add(target);
        }

        _ = SendMessageTimeout(HwndBroadcast, WmFontChange, 0, 0, SmtoAbortIfHung, 2000, out _);
        return installed;
    }

    /// <summary>The face name of the classic console window's font (conhost), or null.</summary>
    public static string? ConsoleFontFace()
    {
        var handle = GetStdHandle(StdOutputHandle);
        if (handle == 0 || handle == -1)
        {
            return null;
        }

        var info = new ConsoleFontInfoEx { Size = (uint)Unsafe.SizeOf<ConsoleFontInfoEx>() };
        if (!GetCurrentConsoleFontEx(handle, false, ref info))
        {
            return null;
        }

        ReadOnlySpan<ushort> name = info.FaceName;
        var face = new string(MemoryMarshal.Cast<ushort, char>(name)).TrimEnd('\0');
        return face.Length > 0 ? face : null;
    }

    private static bool SameContent(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        return fa.Length == fb.Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }

    private const nint HwndBroadcast = 0xffff;
    private const uint WmFontChange = 0x001D;
    private const uint SmtoAbortIfHung = 0x0002;
    private const int StdOutputHandle = -11;

    [InlineArray(32)]
    private struct FaceNameBuffer
    {
        private ushort _element;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ConsoleFontInfoEx
    {
        public uint Size;
        public uint FontIndex;
        public short FontWidth;
        public short FontHeight;
        public uint FontFamily;
        public uint FontWeight;
        public FaceNameBuffer FaceName;
    }

    [LibraryImport("gdi32.dll", EntryPoint = "AddFontResourceW", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int AddFontResource(string fileName);

    [LibraryImport("gdi32.dll", EntryPoint = "RemoveFontResourceW", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveFontResource(string fileName);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint SendMessageTimeout(nint hWnd, uint msg, nint wParam, nint lParam, uint flags, uint timeout, out nint result);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetStdHandle(int handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCurrentConsoleFontEx(nint consoleOutput, [MarshalAs(UnmanagedType.Bool)] bool maximumWindow, ref ConsoleFontInfoEx info);
}
