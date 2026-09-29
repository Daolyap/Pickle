using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pickle.Core.Update;

/// <summary>How this copy of Pickle was installed, which decides how it updates.</summary>
public enum InstallKind
{
    /// <summary>A pickle(.exe) someone downloaded and runs from anywhere: replaced in place.</summary>
    Portable,

    /// <summary>Installed by the MSI: updated by running the new MSI.</summary>
    Msi,

    Scoop,
    Winget,

    /// <summary>A Linux package (RPM): updated through the package manager.</summary>
    Package,

    /// <summary>dotnet run / a build output: nothing to update.</summary>
    Development,
}

/// <summary>A GitHub release: its version and asset download links.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, IReadOnlyDictionary<string, Uri> Assets, Uri Page);

/// <summary>
/// Checks GitHub for a newer Pickle release and downloads the asset for this install, verified against the release's
/// SHA256SUMS.txt. Replacing a portable executable renames the running one aside (Windows allows renaming a running
/// .exe); the leftover is deleted at the next start.
/// </summary>
public sealed class SelfUpdater(HttpMessageHandler? handler = null, string repository = "Daolyap/Pickle") : IDisposable
{
    public const string ChecksumsAsset = "SHA256SUMS.txt";
    public const string OldSuffix = ".old";

    private readonly HttpClient _http = CreateClient(handler);

    public void Dispose() => _http.Dispose();

    public async Task<ReleaseInfo?> LatestAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(new Uri($"https://api.github.com/repos/{repository}/releases/latest"), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return ParseRelease(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The release in a GitHub API response, or null when its tag isn't a version.</summary>
    public static ReleaseInfo? ParseRelease(string json)
    {
        try
        {
            var root = JsonNode.Parse(json);
            var tag = root?["tag_name"]?.GetValue<string>();
            if (tag is null || ParseVersion(tag) is not { } version || root?["html_url"]?.GetValue<string>() is not { } page)
            {
                return null;
            }

            var assets = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in (root["assets"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (asset["name"]?.GetValue<string>() is { } name && asset["browser_download_url"]?.GetValue<string>() is { } url
                    && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                {
                    assets[name] = uri;
                }
            }

            return new ReleaseInfo(version, tag, assets, new Uri(page));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or UriFormatException)
        {
            return null;
        }
    }

    /// <summary>"v0.3.1", "0.3.1-beta+abc" → 0.3.1 (pre-release labels are ignored).</summary>
    public static Version? ParseVersion(string text)
    {
        var core = text.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        return Version.TryParse(core, out var version) ? version : null;
    }

    /// <summary>The release asset this install updates from, or null when it updates some other way.</summary>
    public static string? AssetName(InstallKind kind, Version version, string rid)
    {
        var v = version.ToString(3);
        return kind switch
        {
            InstallKind.Msi => $"pickle-{v}-{rid}.msi",
            InstallKind.Portable => rid.StartsWith("win-", StringComparison.Ordinal) ? $"pickle-{v}-{rid}.exe" : $"pickle-{v}-{rid}.tar.gz",
            _ => null,
        };
    }

    public static InstallKind DetectInstallKind(string? processPath, string? msiInstallDir)
    {
        if (string.IsNullOrEmpty(processPath))
        {
            return InstallKind.Development;
        }

        var name = Path.GetFileNameWithoutExtension(processPath);
        if (!name.Equals("pickle", StringComparison.OrdinalIgnoreCase))
        {
            // dotnet pickle.dll, or a test host.
            return InstallKind.Development;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(processPath)) ?? string.Empty;
        var normalized = directory.Replace('\\', '/');
        if (msiInstallDir is { Length: > 0 } && Path.GetFullPath(msiInstallDir).TrimEnd('\\', '/').Equals(directory.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            return InstallKind.Msi;
        }

        if (normalized.Contains("/scoop/apps/", StringComparison.OrdinalIgnoreCase))
        {
            return InstallKind.Scoop;
        }

        if (normalized.Contains("/Microsoft/WinGet/", StringComparison.OrdinalIgnoreCase))
        {
            return InstallKind.Winget;
        }

        if (normalized is "/usr/bin" or "/usr/local/bin" or "/usr/lib/pickle" or "/opt/pickle")
        {
            return InstallKind.Package;
        }

        return normalized.Contains("/bin/Debug/", StringComparison.OrdinalIgnoreCase) || normalized.Contains("/bin/Release/", StringComparison.OrdinalIgnoreCase)
            ? InstallKind.Development
            : InstallKind.Portable;
    }

    /// <summary>The hex SHA-256 listed for <paramref name="asset"/> in a sha256sum-style file.</summary>
    public static string? FindHash(string checksums, string asset)
    {
        foreach (var raw in checksums.Split('\n'))
        {
            var parts = raw.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*').Equals(asset, StringComparison.Ordinal) && parts[0].Length == 64)
            {
                return parts[0].ToLowerInvariant();
            }
        }

        return null;
    }

    /// <summary>Downloads <paramref name="asset"/> into <paramref name="directory"/> and checks it against the release's checksums.</summary>
    /// <exception cref="InvalidDataException">The asset is missing from the release or its checksum, or doesn't match it.</exception>
    public async Task<string> DownloadVerifiedAsync(ReleaseInfo release, string asset, string directory, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (!release.Assets.TryGetValue(asset, out var assetUri) || !release.Assets.TryGetValue(ChecksumsAsset, out var sumsUri))
        {
            throw new InvalidDataException($"The {release.Tag} release has no {asset} (or no {ChecksumsAsset}).");
        }

        var sums = await _http.GetStringAsync(sumsUri, cancellationToken).ConfigureAwait(false);
        var expected = FindHash(sums, asset) ?? throw new InvalidDataException($"{ChecksumsAsset} doesn't list {asset}.");

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, asset);
        progress?.Report($"Downloading {asset}…");
        using (var response = await _http.GetAsync(assetUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            await using var file = File.Create(path);
            await response.Content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        }

        string actual;
        await using (var file = File.OpenRead(path))
        {
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
        }

        if (!actual.Equals(expected, StringComparison.Ordinal))
        {
            File.Delete(path);
            throw new InvalidDataException($"{asset} doesn't match its published SHA-256 (expected {expected}, got {actual}); nothing was changed.");
        }

        return path;
    }

    /// <summary>The pickle executable inside a downloaded linux/macOS .tar.gz, extracted next to it.</summary>
    public static string ExtractExecutable(string archive)
    {
        var target = Path.Combine(Path.GetDirectoryName(archive)!, "pickle.new");
        using var stream = File.OpenRead(archive);
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile && Path.GetFileName(entry.Name) == "pickle")
            {
                entry.ExtractToFile(target, overwrite: true);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }

                return target;
            }
        }

        throw new InvalidDataException("The archive has no pickle executable.");
    }

    /// <summary>
    /// Puts <paramref name="replacement"/> where <paramref name="current"/> is: the running file is renamed to
    /// "&lt;name&gt;.old" first (put back if the move fails).
    /// </summary>
    public static void ReplaceExecutable(string current, string replacement)
    {
        var old = current + OldSuffix;
        if (File.Exists(old))
        {
            File.Delete(old);
        }

        File.Move(current, old);
        try
        {
            File.Move(replacement, current);
        }
        catch
        {
            File.Move(old, current);
            throw;
        }
    }

    /// <summary>Deletes the previous executable left by an update, once it is no longer running.</summary>
    public static void CleanUpAfterUpdate(string? processPath)
    {
        if (processPath is null)
        {
            return;
        }

        try
        {
            var old = processPath + OldSuffix;
            if (File.Exists(old))
            {
                File.Delete(old);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another Pickle may still be running the old copy; try again next time.
        }
    }

    public static string CurrentRid => RuntimeInformation.RuntimeIdentifier switch
    {
        var rid when rid.StartsWith("win", StringComparison.Ordinal) => "win-" + Arch(),
        var rid when rid.StartsWith("osx", StringComparison.Ordinal) => "osx-" + Arch(),
        _ => "linux-" + Arch(),
    };

    private static string Arch() => RuntimeInformation.ProcessArchitecture.ToString().ToLower(CultureInfo.InvariantCulture);

    private static HttpClient CreateClient(HttpMessageHandler? handler)
    {
        var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromMinutes(10);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Pickle", PickleRuntime.Version));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }
}
