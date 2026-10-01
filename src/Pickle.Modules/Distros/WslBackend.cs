using System.Text.RegularExpressions;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Modules.Distros;

/// <summary>An installable WSL distribution from <c>wsl --list --online</c>.</summary>
public sealed record OnlineDistro(string Name, string Description);

/// <summary>Windows Subsystem for Linux through <c>wsl.exe</c> (also found from inside WSL, where Windows programs are on PATH).</summary>
public sealed partial class WslBackend(Func<IProgramRunner> runnerFactory, TimeProvider? clock = null) : IDistroBackend
{
    public const string Program = "wsl.exe";

    // WSL_UTF8 makes current versions print UTF-8; older ones still print UTF-16, which DistroNames.Clean flattens.
    private static readonly Dictionary<string, string?> WslEnvironment = new() { ["WSL_UTF8"] = "1" };
    private static readonly TimeSpan LongOperation = TimeSpan.FromMinutes(30);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private IProgramRunner Runner => runnerFactory();

    public string Id => "wsl";

    public string DisplayName => "WSL";

    public bool IsAvailable => Runner.Find(Program) is not null;

    public async Task<IReadOnlyList<Distro>> ListAsync(CancellationToken cancellationToken)
    {
        var verbose = await RunAsync(["--list", "--verbose"], cancellationToken).ConfigureAwait(false);
        var distros = Parse(verbose.StdOut, null);
        if (!verbose.Success && distros.Count == 0)
        {
            throw new InvalidOperationException(DistroNames.Lines(verbose.StdOut + "\n" + verbose.StdErr).FirstOrDefault() ?? $"wsl.exe exited with {verbose.ExitCode}.");
        }

        // The state column is translated on non-English Windows; the list of running names is not.
        var running = await RunAsync(["--list", "--running", "--quiet"], cancellationToken).ConfigureAwait(false);
        return running.Success ? Parse(verbose.StdOut, running.StdOut) : distros;
    }

    public async Task<IReadOnlyList<string>> DescribeAsync(Distro distro, CancellationToken cancellationToken)
    {
        if (!distro.IsRunning)
        {
            return [.. distro.Lines, string.Empty, "Stopped: it starts when you open it. Nothing is read from it until then."];
        }

        var release = await RunAsync(["-d", DistroNames.Require(distro.Name), "--exec", "cat", "/etc/os-release"], cancellationToken).ConfigureAwait(false);
        var pretty = release.Success
            ? DistroNames.Lines(release.StdOut).FirstOrDefault(l => l.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))?["PRETTY_NAME=".Length..].Trim('"')
            : null;
        return pretty is null ? distro.Lines : [.. distro.Lines, $"System:  {pretty}"];
    }

    public async Task<IReadOnlyList<OnlineDistro>> ListOnlineAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(["--list", "--online"], cancellationToken).ConfigureAwait(false);
        return result.Success ? ParseOnline(result.StdOut) : throw new InvalidOperationException(DistroNames.Lines(result.StdOut + "\n" + result.StdErr).LastOrDefault() ?? "wsl.exe could not list the online distributions.");
    }

    public BackendCommand? Command(Distro distro, string verb)
    {
        var name = DistroNames.Require(distro.Name);
        return verb switch
        {
            "stop" => Build(["--terminate", name]),
            "remove" => Build(["--unregister", name]),
            "default" => Build(["--set-default", name]),
            _ => null,
        };
    }

    public string? ShellLine(Distro distro, string what)
    {
        var name = DistroNames.Require(distro.Name);
        return what switch
        {
            "enter" => PowerShellQuote.Command(Program, "-d", name),
            "export" => PowerShellQuote.Command(Program, "--export", name, $"{name}-{_clock.GetLocalNow():yyyyMMdd}.tar"),
            _ => null,
        };
    }

    public string CreateLine(string name, string? image) => PowerShellQuote.Command(Program, "--install", "-d", DistroNames.Require(name));

    public static BackendCommand Export(string name, string file) =>
        Build(["--export", DistroNames.Require(name), RequirePath(file)]);

    public static BackendCommand Import(string name, string directory, string file, int? version)
    {
        var arguments = new List<string> { "--import", DistroNames.Require(name), RequirePath(directory), RequirePath(file) };
        if (version is { } v)
        {
            arguments.AddRange(["--version", RequireVersion(v)]);
        }

        return Build([.. arguments]);
    }

    public static BackendCommand SetVersion(string name, int version) => Build(["--set-version", DistroNames.Require(name), RequireVersion(version)]);

    public static BackendCommand Update { get; } = Build(["--update"]);

    public static BackendCommand Shutdown { get; } = Build(["--shutdown"]);

    /// <summary>Rows of <c>wsl -l -v</c>: <c>* Ubuntu  Running  2</c>. <paramref name="running"/> is the quiet list of running names, when known.</summary>
    internal static IReadOnlyList<Distro> Parse(string verbose, string? running)
    {
        var runningNames = running is null
            ? null
            : DistroNames.Lines(running).Where(DistroNames.IsValid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = new List<Distro>();
        foreach (var line in DistroNames.Lines(verbose))
        {
            if (Row().Match(line) is not { Success: true } m)
            {
                continue;
            }

            var name = m.Groups["name"].Value;
            var state = runningNames is not null
                ? runningNames.Contains(name) ? "running" : "stopped"
                : m.Groups["state"].Value.Equals("Running", StringComparison.OrdinalIgnoreCase) ? "running" : "stopped";
            var version = m.Groups["version"].Value;
            var isDefault = m.Groups["default"].Success;
            list.Add(new Distro("wsl", name, state, $"WSL {version}" + (isDefault ? " · default" : string.Empty), isDefault,
            [
                $"Name:    {name}",
                $"State:   {state}",
                $"Version: WSL {version}",
                $"Default: {(isDefault ? "yes" : "no")}",
            ]));
        }

        return [.. list.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)];
    }

    internal static IReadOnlyList<OnlineDistro> ParseOnline(string text)
    {
        var list = new List<OnlineDistro>();
        var inTable = false;
        foreach (var line in DistroNames.Lines(text))
        {
            if (!inTable)
            {
                inTable = line.StartsWith("NAME", StringComparison.OrdinalIgnoreCase) && line.Contains("  ", StringComparison.Ordinal);
                continue;
            }

            if (OnlineRow().Match(line) is { Success: true } m)
            {
                list.Add(new OnlineDistro(m.Groups[1].Value, m.Groups[2].Value.Trim()));
            }
        }

        return list;
    }

    private Task<ProgramResult> RunAsync(string[] arguments, CancellationToken cancellationToken) =>
        Runner.RunAsync(Program, arguments, new ProgramRunOptions { Environment = WslEnvironment, Timeout = TimeSpan.FromSeconds(30) }, cancellationToken);

    private static BackendCommand Build(string[] arguments) =>
        new(Program, arguments, LongOperation) { Environment = WslEnvironment };

    private static string RequirePath(string path) =>
        path.Length == 0 || path[0] == '-' ? throw new ArgumentException($"'{path}' is not a usable path (a leading '-' would be read as an option).") : path;

    private static string RequireVersion(int version) =>
        version is 1 or 2 ? version.ToString(System.Globalization.CultureInfo.InvariantCulture) : throw new ArgumentException("The WSL version is 1 or 2.");

    [GeneratedRegex(@"^(?<default>\*)?\s*(?<name>\S+)\s+(?<state>\S+)\s+(?<version>\d+)$")]
    private static partial Regex Row();

    [GeneratedRegex(@"^(\S+)\s{2,}(.+)$")]
    private static partial Regex OnlineRow();
}
