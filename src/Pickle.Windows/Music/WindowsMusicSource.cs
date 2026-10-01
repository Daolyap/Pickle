using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Pickle.Abstractions.Services;
using Pickle.Windows.Processes;
using Pickle.Windows.Winget;

namespace Pickle.Windows.Music;

/// <summary>
/// The current Windows media session (System Media Transport Controls: Spotify, browsers, Groove, VLC…). WinRT is not
/// reachable from PowerShell 7, so a fixed script runs in Windows PowerShell 5.1 from System32; it only happens in
/// the background job, never from the prompt.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsMusicSource(IProcessRunner runner) : IMusicSource
{
    internal const string Script = """
        $ErrorActionPreference = 'Stop'
        try {
            Add-Type -AssemblyName System.Runtime.WindowsRuntime
            $asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
            function Await($operation, $type) {
                $task = $asTask.MakeGenericMethod($type).Invoke($null, @($operation))
                $task.Wait(-1) | Out-Null
                $task.Result
            }
            [void][Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType = WindowsRuntime]
            $manager = Await ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]::RequestAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager])
            $found = @()
            foreach ($session in $manager.GetSessions()) {
                $properties = Await ($session.TryGetMediaPropertiesAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties])
                $found += [pscustomobject]@{ Title = $properties.Title; Artist = $properties.Artist; State = [string]$session.GetPlaybackInfo().PlaybackStatus; App = $session.SourceAppUserModelId }
            }
            ConvertTo-Json -InputObject @($found) -Compress
        } catch { '[]' }
        """;

    public bool IsSupported => OperatingSystem.IsWindows() && File.Exists(WingetService.WindowsPowerShellPath);

    public async Task<NowPlaying?> GetAsync(string player, CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync(
            WingetService.WindowsPowerShellPath,
            ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Script))],
            null,
            TimeSpan.FromSeconds(10),
            cancellationToken,
            WingetSourceRepair.Environment()).ConfigureAwait(false);
        return result.ExitCode == 0 && !result.TimedOut ? Pick(Parse(result.Output), player) : null;
    }

    public static IReadOnlyList<NowPlaying> Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json.Trim().Length == 0 ? "[]" : json.Trim());
            var items = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.EnumerateArray() : new[] { document.RootElement }.AsEnumerable().Select(e => e);
            return [.. items.Select(e => new NowPlaying(
                    Text(e, "Title"),
                    Text(e, "Artist"),
                    Text(e, "State") switch { "Playing" => PlaybackState.Playing, "Paused" => PlaybackState.Paused, _ => PlaybackState.Stopped },
                    Text(e, "App")))
                .Where(p => p.Title.Length > 0)];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The playing session first, else a paused one; <paramref name="player"/> narrows it to apps whose id contains that text.</summary>
    public static NowPlaying? Pick(IReadOnlyList<NowPlaying> sessions, string player)
    {
        var candidates = player.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? sessions
            : [.. sessions.Where(s => s.Player?.Contains(player, StringComparison.OrdinalIgnoreCase) == true)];
        return candidates.FirstOrDefault(s => s.State == PlaybackState.Playing) ?? candidates.FirstOrDefault();
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
}
