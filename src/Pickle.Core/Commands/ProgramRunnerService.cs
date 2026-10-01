using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Pickle.Abstractions.Services;

namespace Pickle.Core.Commands;

/// <summary><see cref="IProgramRunner"/> over <see cref="ProcessRunner"/> and <see cref="ExecutableLocator"/>.</summary>
public sealed class ProgramRunnerService : IProgramRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    public string? Find(string program) => ExecutableLocator.Find(program);

    public async Task<ProgramResult> RunAsync(string program, IEnumerable<string> arguments, ProgramRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        var result = await ProcessRunner.RunAsync(
            program,
            arguments,
            options?.WorkingDirectory,
            options?.Environment,
            options?.Timeout ?? DefaultTimeout,
            cancellationToken,
            options?.StandardInput).ConfigureAwait(false);
        return new ProgramResult(result.ExitCode, result.StdOut, result.StdErr);
    }

    public async Task<int> StreamAsync(string program, IEnumerable<string> arguments, Action<string> onLine, ProgramRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (ExecutableLocator.Find(program) is not { } executable)
        {
            onLine($"{program} was not found on PATH.");
            return ProgramResult.NotFound;
        }

        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (options?.WorkingDirectory is { } directory)
        {
            psi.WorkingDirectory = directory;
        }

        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in options?.Environment ?? new Dictionary<string, string?>())
        {
            if (value is null)
            {
                psi.Environment.Remove(key);
            }
            else
            {
                psi.Environment[key] = value;
            }
        }

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Win32Exception ex)
        {
            onLine($"{program} could not be started: {ex.Message}");
            return ProgramResult.NotFound;
        }

        if (process is null)
        {
            return ProgramResult.NotFound;
        }

        using (process)
        {
            process.StandardInput.Close();
            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                }
            });
            var pumps = new[] { Pump(process.StandardOutput, onLine), Pump(process.StandardError, onLine) };
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(pumps).ConfigureAwait(false);
            return cancellationToken.IsCancellationRequested ? ProgramResult.TimedOut : process.ExitCode;
        }
    }

    private static async Task Pump(StreamReader reader, Action<string> onLine)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            onLine(line);
        }
    }
}
