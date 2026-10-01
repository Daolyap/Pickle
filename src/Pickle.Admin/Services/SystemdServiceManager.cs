using System.Text.RegularExpressions;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Admin.Services;

/// <summary>systemd units of type service (system and <c>--user</c> scope) through <c>systemctl</c>.</summary>
public sealed partial class SystemdServiceManager(IProgramRunner runner, IPrivilegeService privilege) : ISystemServiceManager
{
    public string Name => "systemd";

    public bool IsSupported => OperatingSystem.IsLinux() && runner.Find("systemctl") is not null;

    public bool HasUserScope => true;

    public async Task<IReadOnlyList<ServiceInfo>> ListAsync(bool userScope, CancellationToken cancellationToken = default)
    {
        var scope = userScope ? new[] { "--user" } : [];
        var units = await runner.RunAsync("systemctl", [.. scope, "list-units", "--type=service", "--all", "--no-legend", "--no-pager", "--plain"], null, cancellationToken).ConfigureAwait(false);
        var files = await runner.RunAsync("systemctl", [.. scope, "list-unit-files", "--type=service", "--no-legend", "--no-pager", "--plain"], null, cancellationToken).ConfigureAwait(false);
        if (!units.WasFound)
        {
            throw new InvalidOperationException("systemctl was not found.");
        }

        return Merge(ParseUnits(units.StdOut), ParseUnitFiles(files.StdOut));
    }

    public async Task<IReadOnlyList<string>> DetailsAsync(string id, bool userScope, CancellationToken cancellationToken = default)
    {
        Require(id);
        var scope = userScope ? new[] { "--user" } : [];
        var status = await runner.RunAsync("systemctl", [.. scope, "status", "--no-pager", "--full", "-n", "15", "--", id], null, cancellationToken).ConfigureAwait(false);
        var lines = (status.StdOut + status.StdErr).Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n').Select(l => l.TrimEnd()).ToList();
        return lines.Count == 0 ? [id] : lines;
    }

    public string? LogsCommand(string id, bool userScope) =>
        IsValidUnit(id) ? PowerShellQuote.Command("journalctl", [.. (userScope ? new[] { "--user" } : []), "-f", "-n", "100", "-u", id]) : null;

    public async Task<ServiceOperationResult> ControlAsync(string id, ServiceAction action, bool userScope, CancellationToken cancellationToken = default)
    {
        Require(id);
        var verb = action switch
        {
            ServiceAction.Start => "start",
            ServiceAction.Stop => "stop",
            ServiceAction.Restart => "restart",
            ServiceAction.Enable => "enable",
            ServiceAction.Disable => "disable",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
        if (userScope)
        {
            var result = await runner.RunAsync("systemctl", ["--user", verb, "--", id], null, cancellationToken).ConfigureAwait(false);
            return new ServiceOperationResult(result.Success, result.Success ? $"{id}: {verb} done." : result.Message);
        }

        var command = new PrivilegedCommand("systemctl", [verb, "--", id], $"{char.ToUpperInvariant(verb[0])}{verb[1..]} {id}");
        var outcome = await privilege.RunAsync(command, cancellationToken).ConfigureAwait(false);
        return new ServiceOperationResult(outcome.Success, outcome.Message)
        {
            ShellCommand = outcome.NeedsTerminal ? privilege.ShellCommand(command) : null,
        };
    }

    // list-units: UNIT LOAD ACTIVE SUB DESCRIPTION (a leading ●/○/× marks problems when not --plain).
    public static IReadOnlyList<(string Id, string Load, string Active, string Sub, string Description)> ParseUnits(string output)
    {
        var rows = new List<(string, string, string, string, string)>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim().TrimStart('●', '○', '×', '*', ' ');
            var match = UnitLine().Match(line);
            if (match.Success)
            {
                rows.Add((match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value, match.Groups[4].Value, match.Groups[5].Value.Trim()));
            }
        }

        return rows;
    }

    // list-unit-files: UNIT FILE, STATE[, PRESET]
    public static IReadOnlyDictionary<string, string> ParseUnitFiles(string output)
    {
        var states = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in output.Split('\n'))
        {
            var parts = raw.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].EndsWith(".service", StringComparison.Ordinal))
            {
                states[parts[0]] = parts[1];
            }
        }

        return states;
    }

    public static IReadOnlyList<ServiceInfo> Merge(
        IReadOnlyList<(string Id, string Load, string Active, string Sub, string Description)> units,
        IReadOnlyDictionary<string, string> files)
    {
        var byId = new Dictionary<string, ServiceInfo>(StringComparer.Ordinal);
        foreach (var (id, load, active, sub, description) in units)
        {
            if (!id.EndsWith(".service", StringComparison.Ordinal) || load == "not-found")
            {
                continue;
            }

            byId[id] = new ServiceInfo(id, description.Length > 0 ? description : id, MapState(active, sub), MapStart(files.GetValueOrDefault(id)), description.Length > 0 ? description + $" ({active}/{sub})" : $"{active}/{sub}");
        }

        foreach (var (id, state) in files)
        {
            byId.TryAdd(id, new ServiceInfo(id, id, ServiceState.Stopped, MapStart(state), "inactive"));
        }

        return [.. byId.Values.OrderBy(s => s.Id, StringComparer.OrdinalIgnoreCase)];
    }

    public static ServiceState MapState(string active, string sub) => active switch
    {
        "active" or "reloading" => ServiceState.Running,
        "activating" => ServiceState.Starting,
        "deactivating" => ServiceState.Stopping,
        "failed" => ServiceState.Failed,
        "inactive" => ServiceState.Stopped,
        _ => sub == "dead" ? ServiceState.Stopped : ServiceState.Unknown,
    };

    public static ServiceStartMode MapStart(string? unitFileState) => unitFileState switch
    {
        "enabled" or "enabled-runtime" => ServiceStartMode.Automatic,
        "disabled" or "masked" or "masked-runtime" => ServiceStartMode.Disabled,
        null => ServiceStartMode.Unknown,
        _ => ServiceStartMode.Manual,
    };

    public static bool IsValidUnit(string id) => UnitName().IsMatch(id);

    private static void Require(string id)
    {
        if (!IsValidUnit(id))
        {
            throw new ArgumentException($"'{id}' is not a service name (letters, digits and : _ . @ - ending in .service).");
        }
    }

    [GeneratedRegex(@"^(\S+)\s+(\S+)\s+(\S+)\s+(\S+)\s*(.*)$")]
    private static partial Regex UnitLine();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9:_.@\\-]{0,200}\.service$")]
    private static partial Regex UnitName();
}
