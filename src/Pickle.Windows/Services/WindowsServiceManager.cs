using System.Management.Automation;
using System.Runtime.Versioning;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Windows.Services;

/// <summary>Windows services: listing through CIM (no elevation), control through the broker's allowlisted <c>ServiceControl</c>.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsServiceManager(Func<IPickleShell> shell, IElevationBroker broker) : ISystemServiceManager
{
    internal const string ListScript = """
        Get-CimInstance -ClassName Win32_Service | ForEach-Object {
            [pscustomobject]@{
                Name = $_.Name; DisplayName = $_.DisplayName; State = $_.State; StartMode = $_.StartMode
                Delayed = [bool]$_.DelayedAutoStart; Description = $_.Description; Account = $_.StartName; Path = $_.PathName
            }
        }
        """;

    internal const string DetailsScript = """
        param($Name)
        $s = Get-CimInstance -ClassName Win32_Service -Filter ("Name='{0}'" -f ($Name -replace "'", "''"))
        if (-not $s) { 'Service not found.'; return }
        'Name:        ' + $s.Name
        'Display name:' + ' ' + $s.DisplayName
        'State:       ' + $s.State + ' (' + $s.StartMode + ' start' + $(if ($s.DelayedAutoStart) { ', delayed' }) + ')'
        'Account:     ' + $s.StartName
        'Path:        ' + $s.PathName
        'Process id:  ' + $s.ProcessId
        'Description: ' + $s.Description
        $svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
        if ($svc) {
            'Depends on:  ' + (($svc.ServicesDependedOn | ForEach-Object Name) -join ', ')
            'Needed by:   ' + (($svc.DependentServices | ForEach-Object Name) -join ', ')
        }
        """;

    public string Name => "Windows services";

    public bool IsSupported => OperatingSystem.IsWindows();

    public bool HasUserScope => false;

    public async Task<IReadOnlyList<ServiceInfo>> ListAsync(bool userScope, CancellationToken cancellationToken = default)
    {
        var result = await shell().InvokeAsync(ListScript, null, ShellTarget.Background, cancellationToken).ConfigureAwait(false);
        if (result.HadErrors && result.Output.Count == 0)
        {
            throw new InvalidOperationException(result.Errors[0].Exception.Message);
        }

        return [.. result.Output.Select(Map).OfType<ServiceInfo>().OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<IReadOnlyList<string>> DetailsAsync(string id, bool userScope, CancellationToken cancellationToken = default)
    {
        Require(id);
        var result = await shell().InvokeAsync(DetailsScript, new Dictionary<string, object?> { ["Name"] = id }, ShellTarget.Background, cancellationToken).ConfigureAwait(false);
        return [.. result.Output.Select(o => o.ToString())];
    }

    public string? LogsCommand(string id, bool userScope) =>
        WindowsIds.IsValidServiceName(id)
            ? "Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Service Control Manager' } -MaxEvents 300 | Where-Object { $_.Message -like " + PowerShellQuote.Single("*" + id + "*") + " } | Format-List TimeCreated, Id, Message"
            : null;

    public async Task<ServiceOperationResult> ControlAsync(string id, ServiceAction action, bool userScope, CancellationToken cancellationToken = default)
    {
        Require(id);
        var verb = action switch
        {
            ServiceAction.Start => "start",
            ServiceAction.Stop => "stop",
            ServiceAction.Restart => "restart",
            ServiceAction.Enable => "automatic",
            ServiceAction.Disable => "manual",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
        try
        {
            var responses = await broker.RunAsync([new ElevatedRequest(ElevatedOperationKind.ServiceControl, [id, verb])], null, cancellationToken).ConfigureAwait(false);
            var response = responses.FirstOrDefault();
            return new ServiceOperationResult(response?.Success ?? false, response?.Message ?? "No answer from the elevated helper.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ServiceOperationResult(false, "Administrator approval was declined.");
        }
    }

    internal static ServiceInfo? Map(PSObject row)
    {
        var name = Text(row, "Name");
        if (name is null)
        {
            return null;
        }

        var state = Text(row, "State") switch
        {
            "Running" => ServiceState.Running,
            "Stopped" => ServiceState.Stopped,
            "Start Pending" or "Continue Pending" => ServiceState.Starting,
            "Stop Pending" or "Pause Pending" => ServiceState.Stopping,
            "Paused" => ServiceState.Paused,
            _ => ServiceState.Unknown,
        };
        var mode = Text(row, "StartMode") switch
        {
            "Auto" or "Boot" or "System" => ServiceStartMode.Automatic,
            "Manual" => ServiceStartMode.Manual,
            "Disabled" => ServiceStartMode.Disabled,
            _ => ServiceStartMode.Unknown,
        };
        return new ServiceInfo(name, Text(row, "DisplayName") ?? name, state, mode, Text(row, "Description"), Text(row, "Account"), Text(row, "Path"));
    }

    private static string? Text(PSObject row, string property) =>
        row.Properties[property]?.Value?.ToString() is { Length: > 0 } text ? text : null;

    private static void Require(string id)
    {
        if (!WindowsIds.IsValidServiceName(id))
        {
            throw new ArgumentException($"'{id}' is not a valid service name.");
        }
    }
}
