using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Core.Update;

namespace Pickle.Core.Hosting;

/// <summary>One-line notices under the banner, from what the background jobs cached (nothing is fetched here).</summary>
internal static class StartupNotices
{
    private static readonly TimeSpan WingetFreshFor = TimeSpan.FromDays(2);

    public static IReadOnlyList<string> Lines(PickleRuntime runtime, DateTimeOffset now)
    {
        var lines = new List<string>();
        if (runtime.Config.Current.Shell.CheckForUpdates && UpdateCheck.Newer(runtime.Background, PickleRuntime.Version) is { } release)
        {
            lines.Add($"Pickle {release.Version} is available · pk version update");
        }

        if (runtime.Background.Read<List<WingetPackage>>(WingetCache.UpgradesKey) is { } cached && now - cached.At < WingetFreshFor && cached.Value.Count > 0)
        {
            var key = runtime.KeyBindingRegistry.Bindings.FirstOrDefault(b => b.Value == Pickle.Abstractions.EditorActionNames.PanelWinget).Key ?? "pk winget";
            lines.Add($"{cached.Value.Count} app upgrade{(cached.Value.Count == 1 ? string.Empty : "s")} available · {key}");
        }

        return lines;
    }

    public static void Write(PickleRuntime runtime)
    {
        var color = runtime.Themes.Current.Ui.Muted;
        foreach (var line in Lines(runtime, DateTimeOffset.UtcNow))
        {
            runtime.Terminal.Write(Ansi.Colorize("↑ " + line, color) + "\r\n");
        }
    }
}
