namespace Pickle.Core.Commands;

/// <summary>
/// Resolves program names to full paths. Never hand a bare name to <c>Process.Start</c>: CreateProcess (Windows) and
/// .NET's Unix resolver both try the current directory, i.e. whatever a cloned repo or extracted archive put there.
/// Only fully qualified PATH entries are searched.
/// </summary>
public static class ExecutableLocator
{
    public static string? Find(string name, string? pathVariable = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (Path.IsPathFullyQualified(name))
        {
            return IsExecutableFile(name) ? name : null;
        }

        if (name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal))
        {
            return null;
        }

        string[] extensions = OperatingSystem.IsWindows() && !Path.HasExtension(name)
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [string.Empty];
        pathVariable ??= Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var entry in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var directory = entry.Trim('"');
            if (!Path.IsPathFullyQualified(directory))
            {
                continue;
            }

            foreach (var extension in extensions)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(directory, name + extension);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (IsExecutableFile(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>A program from the Windows system directory (cmd.exe, notepad.exe), never looked up anywhere else.</summary>
    public static string SystemProgram(string fileName) => Path.Combine(Environment.SystemDirectory, fileName);

    internal static bool IsExecutableFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            if (OperatingSystem.IsWindows())
            {
                return true;
            }

            const UnixFileMode anyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (File.GetUnixFileMode(path) & anyExecute) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
