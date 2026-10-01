namespace Pickle.Abstractions.Services;

/// <summary>What a finished program printed. <see cref="NotFound"/> and <see cref="TimedOut"/> are returned as exit codes.</summary>
public sealed record ProgramResult(int ExitCode, string StdOut, string StdErr)
{
    public const int NotFound = -2;
    public const int TimedOut = -3;

    public bool Success => ExitCode == 0;

    public bool WasFound => ExitCode != NotFound;

    /// <summary>The most useful single line to show when the program failed.</summary>
    public string Message
    {
        get
        {
            var text = string.IsNullOrWhiteSpace(StdErr) ? StdOut : StdErr;
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? $"exit code {ExitCode}";
        }
    }
}

/// <summary>Per-call settings for <see cref="IProgramRunner"/>.</summary>
public sealed record ProgramRunOptions
{
    public string? WorkingDirectory { get; init; }

    /// <summary>Variables for the child (null value: remove it).</summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }

    /// <summary>Default: 60 seconds.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Written to the program's standard input, then closed (passwords go here, never on the command line).</summary>
    public string? StandardInput { get; init; }
}

/// <summary>
/// Runs external programs for plugins and modules: an argument list (never a shell string), the program resolved from
/// absolute PATH entries only (never the current directory), captured output and a timeout.
/// </summary>
public interface IProgramRunner
{
    /// <summary>The full path of <paramref name="program"/> on PATH, or null.</summary>
    string? Find(string program);

    Task<ProgramResult> RunAsync(string program, IEnumerable<string> arguments, ProgramRunOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Runs until it exits or is cancelled, calling <paramref name="onLine"/> for each line of output (stdout and stderr), e.g. <c>docker logs -f</c>.</summary>
    Task<int> StreamAsync(string program, IEnumerable<string> arguments, Action<string> onLine, ProgramRunOptions? options = null, CancellationToken cancellationToken = default);
}
