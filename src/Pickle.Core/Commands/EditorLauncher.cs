using System.Diagnostics;

namespace Pickle.Core.Commands;

/// <summary>Opens a file in the user's editor: $VISUAL, $EDITOR, VS Code, Notepad (Windows), nano or vi.</summary>
public static class EditorLauncher
{
    private static readonly string[] GuiEditors = ["code", "code-insiders", "cursor", "codium", "notepad", "notepad++", "subl", "gedit", "kate", "zed"];

    public sealed record EditorCommand(string FileName, IReadOnlyList<string> Arguments, bool Wait);

    public static EditorCommand? Resolve(Func<string, string?> getEnv, Func<string, string?> findOnPath, bool isWindows)
    {
        foreach (var variable in new[] { "VISUAL", "EDITOR" })
        {
            var value = getEnv(variable);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var parts = SplitCommandLine(value);
            if (parts.Count == 0)
            {
                continue;
            }

            var exe = findOnPath(parts[0]) ?? (File.Exists(parts[0]) ? parts[0] : null);
            if (exe is not null)
            {
                return Create(exe, parts.Skip(1).ToList());
            }
        }

        var candidates = isWindows ? new[] { "code", "notepad" } : ["code", "nano", "vi"];
        foreach (var candidate in candidates)
        {
            if (findOnPath(candidate) is { } exe)
            {
                return Create(exe, []);
            }
        }

        return null;
    }

    /// <summary>Open <paramref name="file"/>. Terminal editors run in the foreground; GUI editors are started and left running.</summary>
    public static bool TryOpen(string file, out string message)
    {
        var editor = Resolve(Environment.GetEnvironmentVariable, FindOnPath, OperatingSystem.IsWindows());
        if (editor is null)
        {
            message = $"No editor found (set $env:EDITOR). File: {file}";
            return false;
        }

        try
        {
            var isScript = OperatingSystem.IsWindows() && Path.GetExtension(editor.FileName).ToLowerInvariant() is ".cmd" or ".bat";
            var psi = new ProcessStartInfo(editor.FileName) { UseShellExecute = isScript };
            if (isScript)
            {
                // .cmd shims (code.cmd) can't take ArgumentList; the only argument is our own config path.
                psi.Arguments = string.Join(' ', editor.Arguments.Append(file).Select(a => "\"" + a.Replace("\"", string.Empty, StringComparison.Ordinal) + "\""));
            }
            else
            {
                foreach (var argument in editor.Arguments)
                {
                    psi.ArgumentList.Add(argument);
                }

                psi.ArgumentList.Add(file);
            }

            using var process = Process.Start(psi);
            if (editor.Wait)
            {
                process?.WaitForExit();
            }

            message = $"Opened {file} in {Path.GetFileNameWithoutExtension(editor.FileName)}";
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            message = $"Could not start {editor.FileName}: {ex.Message}. File: {file}";
            return false;
        }
    }

    public static string? FindOnPath(string name)
    {
        if (Path.IsPathRooted(name))
        {
            return File.Exists(name) ? name : null;
        }

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries).Prepend(string.Empty)
            : [string.Empty];
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                try
                {
                    var candidate = Path.Combine(dir, name + extension);
                    if (File.Exists(candidate) && (extension.Length > 0 || !OperatingSystem.IsWindows() || Path.HasExtension(name)))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                }
            }
        }

        return null;
    }

    private static EditorCommand Create(string exe, List<string> args)
    {
        var name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
        var waitRequested = args.Any(a => a is "--wait" or "-w");
        var wait = waitRequested || !GuiEditors.Contains(name);
        return new EditorCommand(exe, args, wait);
    }

    internal static List<string> SplitCommandLine(string text)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var quote = '\0';
        foreach (var c in text)
        {
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts;
    }
}
