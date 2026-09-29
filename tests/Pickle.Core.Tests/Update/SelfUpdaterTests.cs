using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Pickle.Core.Commands;
using Pickle.Core.Update;
using Pickle.Testing;

namespace Pickle.Core.Tests.Update;

public sealed class SelfUpdaterTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pickle-update").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("v0.3.1", "0.3.1")]
    [InlineData("0.4.0-beta.2+abc", "0.4.0")]
    [InlineData("V1.2", "1.2")]
    [InlineData("latest", null)]
    public void VersionsComeFromTags(string tag, string? expected) =>
        Assert.Equal(expected, SelfUpdater.ParseVersion(tag)?.ToString());

    [Fact]
    public void ReleasesParseAndOnlyHttpsAssetsCount()
    {
        var release = SelfUpdater.ParseRelease("""
            { "tag_name": "v0.3.0", "html_url": "https://github.com/Daolyap/Pickle/releases/tag/v0.3.0",
              "assets": [ { "name": "pickle-0.3.0-win-x64.exe", "browser_download_url": "https://example.test/a.exe" },
                          { "name": "evil.exe", "browser_download_url": "http://example.test/b.exe" } ] }
            """)!;
        Assert.Equal(new Version(0, 3, 0), release.Version);
        Assert.Equal(["pickle-0.3.0-win-x64.exe"], release.Assets.Keys);
        Assert.Null(SelfUpdater.ParseRelease("{ \"tag_name\": \"nightly\" }"));
        Assert.Null(SelfUpdater.ParseRelease("not json"));
    }

    [Fact]
    public void AssetsAndInstallKinds()
    {
        var v = new Version(0, 3, 0);
        Assert.Equal("pickle-0.3.0-win-x64.exe", SelfUpdater.AssetName(InstallKind.Portable, v, "win-x64"));
        Assert.Equal("pickle-0.3.0-linux-x64.tar.gz", SelfUpdater.AssetName(InstallKind.Portable, v, "linux-x64"));
        Assert.Equal("pickle-0.3.0-win-arm64.msi", SelfUpdater.AssetName(InstallKind.Msi, v, "win-arm64"));
        Assert.Null(SelfUpdater.AssetName(InstallKind.Scoop, v, "win-x64"));

        string P(params string[] parts) => Path.Combine([_root, .. parts]);
        Assert.Equal(InstallKind.Msi, SelfUpdater.DetectInstallKind(P("Program Files", "Pickle", "pickle.exe"), P("Program Files", "Pickle")));
        Assert.Equal(InstallKind.Scoop, SelfUpdater.DetectInstallKind(P("scoop", "apps", "pickle", "current", "pickle.exe"), null));
        Assert.Equal(InstallKind.Winget, SelfUpdater.DetectInstallKind(P("AppData", "Local", "Microsoft", "WinGet", "Packages", "Daolyap.Pickle_x", "pickle.exe"), null));
        Assert.Equal(InstallKind.Development, SelfUpdater.DetectInstallKind(P("src", "Pickle", "bin", "Debug", "net10.0", "pickle"), null));
        Assert.Equal(InstallKind.Development, SelfUpdater.DetectInstallKind(P("dotnet", "dotnet"), null));
        Assert.Equal(InstallKind.Portable, SelfUpdater.DetectInstallKind(P("tools", "pickle"), P("Program Files", "Pickle")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(InstallKind.Package, SelfUpdater.DetectInstallKind("/usr/bin/pickle", null));
        }
    }

    [Fact]
    public void ChecksumsAreFoundByExactName()
    {
        var sums = $"{new string('a', 64)}  pickle-0.3.0-win-x64.exe\n{new string('b', 64)} *pickle-0.3.0-win-x64.exe.zip\n";
        Assert.Equal(new string('a', 64), SelfUpdater.FindHash(sums, "pickle-0.3.0-win-x64.exe"));
        Assert.Equal(new string('b', 64), SelfUpdater.FindHash(sums, "pickle-0.3.0-win-x64.exe.zip"));
        Assert.Null(SelfUpdater.FindHash(sums, "pickle-0.3.0-win-arm64.exe"));
    }

    [Fact]
    public async Task DownloadsAreVerifiedAgainstTheChecksums()
    {
        var server = new FakeReleaseServer("99.0.0");
        using var updater = new SelfUpdater(server);
        var release = (await updater.LatestAsync(default))!;
        var asset = server.PortableAsset;

        var file = await updater.DownloadVerifiedAsync(release, asset, Path.Combine(_root, "ok"), null, default);
        Assert.True(File.Exists(file));

        server.Tamper = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => updater.DownloadVerifiedAsync(release, asset, Path.Combine(_root, "bad"), null, default));
        Assert.False(File.Exists(Path.Combine(_root, "bad", asset)));
        await Assert.ThrowsAsync<InvalidDataException>(() => updater.DownloadVerifiedAsync(release, "pickle-99.0.0-nope.exe", _root, null, default));
    }

    [Fact]
    public void ReplacingMovesTheRunningFileAsideAndCleanupRemovesIt()
    {
        var current = Path.Combine(_root, "pickle");
        var replacement = Path.Combine(_root, "pickle.new");
        File.WriteAllText(current, "OLD");
        File.WriteAllText(replacement, "NEW");

        SelfUpdater.ReplaceExecutable(current, replacement);
        Assert.Equal("NEW", File.ReadAllText(current));
        Assert.Equal("OLD", File.ReadAllText(current + SelfUpdater.OldSuffix));

        SelfUpdater.CleanUpAfterUpdate(current);
        Assert.False(File.Exists(current + SelfUpdater.OldSuffix));
    }

    [Fact]
    public void TheExecutableIsExtractedFromATarball()
    {
        var archive = Path.Combine(_root, "pickle.tar.gz");
        File.WriteAllBytes(archive, FakeReleaseServer.TarGz("NEW"));
        Assert.Equal("NEW", File.ReadAllText(SelfUpdater.ExtractExecutable(archive)));
    }

    [Fact]
    public void PkVersionCheckAndUpdateReplaceAPortableInstall()
    {
        using var t = TestPickle.Create(width: 140, height: 40, start: true);
        var server = new FakeReleaseServer("99.0.0");
        var exe = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "tools")).FullName, OperatingSystem.IsWindows() ? "pickle.exe" : "pickle");
        File.WriteAllText(exe, "OLD");
        t.Runtime.CommandRegistry.Register(new VersionCommand(t.Runtime, () => new SelfUpdater(server)) { ProcessPath = () => exe, MsiInstallDir = () => null });

        t.Run("pk version check");
        Assert.Contains("Pickle 99.0.0 is available", t.Terminal.GetScreenText(), StringComparison.Ordinal);
        Assert.Contains("pk version update", t.Terminal.GetScreenText(), StringComparison.Ordinal);

        Assert.ThrowsAny<Exception>(() => t.Run("pk version update"));
        Assert.Equal("OLD", File.ReadAllText(exe));

        t.Run("pk version update --yes");
        Assert.Equal("NEW", File.ReadAllText(exe));
        Assert.Contains("Updated to Pickle 99.0.0", t.Terminal.GetScreenText(), StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedInstallsArePointedAtTheirPackageManagerAndCurrentOnesAreUpToDate()
    {
        using var t = TestPickle.Create(width: 140, height: 40, start: true);
        var scoop = Path.Combine(_root, "scoop", "apps", "pickle", "current", "pickle.exe");
        t.Runtime.CommandRegistry.Register(new VersionCommand(t.Runtime, () => new SelfUpdater(new FakeReleaseServer("99.0.0"))) { ProcessPath = () => scoop });
        t.Run("pk version update --yes");
        Assert.Contains("scoop update pickle", t.Terminal.GetScreenText(), StringComparison.Ordinal);

        using var current = TestPickle.Create(width: 140, height: 40, start: true);
        current.Runtime.CommandRegistry.Register(new VersionCommand(current.Runtime, () => new SelfUpdater(new FakeReleaseServer("0.0.1"))));
        current.Run("pk version check");
        Assert.Contains("is up to date", current.Terminal.GetScreenText(), StringComparison.Ordinal);
    }

    [Fact]
    public void MsiInstallsRunTheVerifiedInstallerAndExit()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var t = TestPickle.Create(width: 140, height: 40, start: true);
        var dir = Directory.CreateDirectory(Path.Combine(_root, "Program Files", "Pickle")).FullName;
        string? started = null;
        t.Runtime.CommandRegistry.Register(new VersionCommand(t.Runtime, () => new SelfUpdater(new FakeReleaseServer("99.0.0")))
        {
            ProcessPath = () => Path.Combine(dir, "pickle.exe"),
            MsiInstallDir = () => dir,
            StartInstaller = path => started = path,
        });

        t.Run("pk version update --yes");
        Assert.EndsWith(".msi", started, StringComparison.Ordinal);
        Assert.True(t.Runtime.ExitRequested);
    }

    [Fact]
    public async Task TheDailyCheckCachesTheLatestReleaseForTheBanner()
    {
        using var t = TestPickle.Create();
        UpdateCheck.Register(t.Runtime, () => new SelfUpdater(new FakeReleaseServer("99.0.0")));

        await t.Runtime.Background.RunOnceAsync(CancellationToken.None);

        Assert.Equal("99.0.0", UpdateCheck.Newer(t.Runtime.Background, PickleRuntime.Version)?.Version);
        Assert.Contains("Pickle 99.0.0 is available · pk version update", Pickle.Core.Hosting.StartupNotices.Lines(t.Runtime, DateTimeOffset.UtcNow));
        Assert.Null(UpdateCheck.Newer(t.Runtime.Background, "99.0.0"));
    }

    private sealed class FakeReleaseServer : HttpMessageHandler
    {
        private readonly string _version;
        private readonly Dictionary<string, byte[]> _assets = [];

        public FakeReleaseServer(string version)
        {
            _version = version;
            var rid = SelfUpdater.CurrentRid;
            _assets[$"pickle-{version}-{rid}.exe"] = Encoding.UTF8.GetBytes("NEW");
            _assets[$"pickle-{version}-{rid}.tar.gz"] = TarGz("NEW");
            _assets[$"pickle-{version}-{rid}.msi"] = Encoding.UTF8.GetBytes("MSI");
            PortableAsset = SelfUpdater.AssetName(InstallKind.Portable, SelfUpdater.ParseVersion(version)!, rid)!;
        }

        public string PortableAsset { get; }

        public bool Tamper { get; set; }

        public static byte[] TarGz(string content)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            using (var tar = new TarWriter(gzip))
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, "pickle") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) };
                tar.WriteEntry(entry);
            }

            return output.ToArray();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/releases/latest", StringComparison.Ordinal))
            {
                var assets = string.Join(",", _assets.Keys.Append(SelfUpdater.ChecksumsAsset)
                    .Select(name => $"{{\"name\":\"{name}\",\"browser_download_url\":\"https://downloads.test/{name}\"}}"));
                return Reply($"{{\"tag_name\":\"v{_version}\",\"html_url\":\"https://github.com/Daolyap/Pickle/releases/tag/v{_version}\",\"assets\":[{assets}]}}");
            }

            var name = Path.GetFileName(path);
            if (name == SelfUpdater.ChecksumsAsset)
            {
                return Reply(string.Concat(_assets.Select(a => $"{Convert.ToHexStringLower(SHA256.HashData(a.Value))}  {a.Key}\n")));
            }

            return _assets.TryGetValue(name, out var bytes)
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Tamper ? [.. bytes, 0] : bytes) })
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Reply(string text) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text) });
    }
}
