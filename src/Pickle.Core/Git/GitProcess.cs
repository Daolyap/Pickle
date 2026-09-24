using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Pickle.Core.Git;

internal sealed record GitProcessResult(int ExitCode, string Output, string Error, bool TimedOut)
{
    public bool Success => ExitCode == 0 && !TimedOut;
}

/// <summary>Runs git as a child process: argv list only (never a shell), UTF-8 pipes, timeout + tree kill.</summary>
internal static class GitProcess
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static async Task<GitProcessResult> RunAsync(
        string gitPath,
        string workingDirectory,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string?> environment,
        string? stdin,
        CancellationToken cancellationToken)
    {
        // A fast git can exit before WaitForExitAsync observes a token that was already cancelled.
        cancellationToken.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo(gitPath)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        // Never let git (or ssh/https helpers it spawns) block on a terminal prompt; GUI credential managers still work.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_MERGE_AUTOEDIT"] = "no";
        foreach (var (name, value) in environment)
        {
            if (value is null)
            {
                psi.Environment.Remove(name);
            }
            else
            {
                psi.Environment[name] = value;
            }
        }

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
            {
                return new GitProcessResult(-1, string.Empty, "git could not be started.", false);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new GitProcessResult(-1, string.Empty, $"git could not be started: {ex.Message}", false);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            if (!string.IsNullOrEmpty(stdin))
            {
                await process.StandardInput.WriteAsync(stdin.AsMemory(), CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            // git exited before reading all of stdin; its exit code and stderr explain why.
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (IOException)
            {
            }
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            cancellationToken.ThrowIfCancellationRequested();
            var verb = psi.ArgumentList.FirstOrDefault(a => !a.StartsWith('-') && !a.Contains('=', StringComparison.Ordinal)) ?? "command";
            return new GitProcessResult(-1, string.Empty, $"git {verb} timed out after {timeout.TotalSeconds:0} s.", true);
        }

        var output = await stdoutTask.ConfigureAwait(false);
        var error = await stderrTask.ConfigureAwait(false);
        return new GitProcessResult(process.ExitCode, output, error, false);
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
        }
    }

    /// <summary>
    /// Absolute path of the git executable, or null. Relative PATH entries are ignored so a repository can't plant
    /// its own git.exe in the current directory.
    /// </summary>
    public static string? Locate(string? pathVariable = null)
    {
        pathVariable ??= Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var fileName = OperatingSystem.IsWindows() ? "git.exe" : "git";
        foreach (var entry in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var directory = entry.Trim('"');
            if (!Path.IsPathFullyQualified(directory))
            {
                continue;
            }

            var candidate = Path.Combine(directory, fileName);
            if (IsExecutableFile(candidate))
            {
                return candidate;
            }
        }

        if (OperatingSystem.IsWindows())
        {
            string?[] roots =
            [
                Environment.GetEnvironmentVariable("ProgramW6432"),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                @"C:\Program Files",
            ];
            foreach (var root in roots)
            {
                if (!string.IsNullOrEmpty(root) && Path.Combine(root, "Git", "cmd", "git.exe") is var candidate && File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static bool IsExecutableFile(string path)
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
}
