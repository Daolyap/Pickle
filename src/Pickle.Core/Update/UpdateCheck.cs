using Pickle.Abstractions.Services;

namespace Pickle.Core.Update;

/// <summary>A daily background look at the latest Pickle release (primary instance only); the banner reads the result.</summary>
internal static class UpdateCheck
{
    public const string Key = "pickle-release";

    public sealed record LatestRelease(string Version, string Page);

    public static void Register(PickleRuntime runtime, Func<SelfUpdater>? updater = null)
    {
        if (!runtime.Config.Current.Shell.CheckForUpdates)
        {
            return;
        }

        runtime.Background.Register(new BackgroundJob(Key, async ct =>
        {
            using var client = updater?.Invoke() ?? new SelfUpdater();
            if (await client.LatestAsync(ct).ConfigureAwait(false) is { } release)
            {
                runtime.Background.Write(Key, new LatestRelease(release.Version.ToString(3), release.Page.ToString()));
            }
        })
        {
            Interval = TimeSpan.FromDays(1),
            InitialDelay = TimeSpan.FromSeconds(30),
        });
    }

    /// <summary>The newer release another check found, or null when this one is current (or nothing is known).</summary>
    public static LatestRelease? Newer(IBackgroundWork background, string currentVersion)
    {
        if (background.Read<LatestRelease>(Key)?.Value is not { } latest
            || SelfUpdater.ParseVersion(latest.Version) is not { } available
            || SelfUpdater.ParseVersion(currentVersion) is not { } current)
        {
            return null;
        }

        return available > current ? latest : null;
    }
}
