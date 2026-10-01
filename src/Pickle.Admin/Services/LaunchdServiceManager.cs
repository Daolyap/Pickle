using System.Globalization;
using System.Text.RegularExpressions;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Admin.Services;

/// <summary>launchd jobs on macOS (best effort): the user's GUI domain without privileges, the system domain through <see cref="IPrivilegeService"/>.</summary>
public sealed partial class LaunchdServiceManager(IProgramRunner runner, IPrivilegeService privilege, Func<int>? userId = null) : ISystemServiceManager
{
    private readonly Func<int> _uid = userId ?? DefaultUserId;

    private static int DefaultUserId() =>
        int.TryParse(Environment.GetEnvironmentVariable("UID"), CultureInfo.InvariantCulture, out var uid) ? uid : 501;

    public string Name => "launchd";

    public bool IsSupported => OperatingSystem.IsMacOS() && runner.Find("launchctl") is not null;

    public bool HasUserScope => true;

    public async Task<IReadOnlyList<ServiceInfo>> ListAsync(bool userScope, CancellationToken cancellationToken = default)
    {
        // `launchctl list` shows the caller's domain; for the system domain ask as root through `print system`.
        var result = userScope
            ? await runner.RunAsync("launchctl", ["list"], null, cancellationToken).ConfigureAwait(false)
            : await runner.RunAsync("launchctl", ["print", "system"], null, cancellationToken).ConfigureAwait(false);
        return userScope ? ParseList(result.StdOut) : ParsePrintSystem(result.StdOut);
    }

    public async Task<IReadOnlyList<string>> DetailsAsync(string id, bool userScope, CancellationToken cancellationToken = default)
    {
        Require(id);
        var result = await runner.RunAsync("launchctl", ["print", Domain(userScope) + "/" + id], null, cancellationToken).ConfigureAwait(false);
        return [.. (result.StdOut + result.StdErr).Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n').Take(60)];
    }

    public string? LogsCommand(string id, bool userScope) =>
        IsValidLabel(id) ? PowerShellQuote.Command("log", "stream", "--predicate", $"subsystem == \"{id}\" OR process == \"{id}\"") : null;

    public async Task<ServiceOperationResult> ControlAsync(string id, ServiceAction action, bool userScope, CancellationToken cancellationToken = default)
    {
        Require(id);
        var target = Domain(userScope) + "/" + id;
        string[] arguments = action switch
        {
            ServiceAction.Start => ["kickstart", target],
            ServiceAction.Stop => ["kill", "SIGTERM", target],
            ServiceAction.Restart => ["kickstart", "-k", target],
            ServiceAction.Enable => ["enable", target],
            ServiceAction.Disable => ["disable", target],
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
        if (userScope)
        {
            var result = await runner.RunAsync("launchctl", arguments, null, cancellationToken).ConfigureAwait(false);
            return new ServiceOperationResult(result.Success, result.Success ? $"{id}: {action} done." : result.Message);
        }

        var command = new PrivilegedCommand("launchctl", arguments, $"{action} {id}");
        var outcome = await privilege.RunAsync(command, cancellationToken).ConfigureAwait(false);
        return new ServiceOperationResult(outcome.Success, outcome.Message) { ShellCommand = outcome.NeedsTerminal ? privilege.ShellCommand(command) : null };
    }

    // PID<tab>Status<tab>Label ("-" when not running; Status is the last exit code).
    public static IReadOnlyList<ServiceInfo> ParseList(string output)
    {
        var rows = new List<ServiceInfo>();
        foreach (var line in output.Split('\n').Skip(1))
        {
            var parts = line.Split('\t', StringSplitOptions.TrimEntries);
            if (parts.Length < 3 || !IsValidLabel(parts[2]))
            {
                continue;
            }

            var running = int.TryParse(parts[0], CultureInfo.InvariantCulture, out var pid) && pid > 0;
            var status = int.TryParse(parts[1], CultureInfo.InvariantCulture, out var code) ? code : 0;
            rows.Add(new ServiceInfo(parts[2], parts[2], running ? ServiceState.Running : status != 0 ? ServiceState.Failed : ServiceState.Stopped, ServiceStartMode.Unknown, running ? $"pid {pid}" : $"last exit {status}"));
        }

        return [.. rows.OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase)];
    }

    // `launchctl print system` lists "services = { <pid> <status> <label> … }".
    public static IReadOnlyList<ServiceInfo> ParsePrintSystem(string output)
    {
        var rows = new List<ServiceInfo>();
        var inServices = false;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("services = {", StringComparison.Ordinal))
            {
                inServices = true;
                continue;
            }

            if (inServices && line == "}")
            {
                break;
            }

            if (inServices && PrintLine().Match(line) is { Success: true } m && IsValidLabel(m.Groups[3].Value))
            {
                var running = m.Groups[1].Value != "-";
                rows.Add(new ServiceInfo(m.Groups[3].Value, m.Groups[3].Value, running ? ServiceState.Running : ServiceState.Stopped, ServiceStartMode.Unknown, running ? "pid " + m.Groups[1].Value : null));
            }
        }

        return [.. rows.OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase)];
    }

    public static bool IsValidLabel(string id) => Label().IsMatch(id);

    private string Domain(bool userScope) => userScope ? "gui/" + _uid().ToString(CultureInfo.InvariantCulture) : "system";

    private static void Require(string id)
    {
        if (!IsValidLabel(id))
        {
            throw new ArgumentException($"'{id}' is not a launchd label.");
        }
    }

    [GeneratedRegex(@"^(\S+)\s+(\S+)\s+(\S+)$")]
    private static partial Regex PrintLine();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._@:\\-]{0,200}$")]
    private static partial Regex Label();
}
