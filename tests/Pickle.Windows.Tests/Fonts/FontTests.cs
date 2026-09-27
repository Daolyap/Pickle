using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Pickle.Abstractions.Services;
using Pickle.Windows.Fonts;
using Pickle.Windows.Terminal;

namespace Pickle.Windows.Tests.Fonts;

public sealed class FontTests : IDisposable
{
    private static readonly string[] Registered =
    [
        "Arial (TrueType)",
        "Cambria & Cambria Math (TrueType)",
        "Cascadia Code NF Regular (TrueType)",
        "Cascadia Code NF Italic (TrueType)",
        "JetBrainsMono NFM SemiBold Italic (TrueType)",
        "FiraCode Nerd Font Mono Retina (TrueType)",
        "Consolas Bold (TrueType)",
    ];

    private readonly string _root = Directory.CreateTempSubdirectory("pickle-fonts").FullName;

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
    [InlineData("Cascadia Code NF", true)]
    [InlineData("cascadia code nf", true)]
    [InlineData("Cascadia Code", false)]
    [InlineData("Cambria Math", true)]
    [InlineData("Consolas", true)]
    [InlineData("JetBrainsMono NFM", true)]
    [InlineData("JetBrainsMono", false)]
    [InlineData("Hack NF", false)]
    public void CatalogMatchesFamiliesAgainstRegisteredFaces(string family, bool installed) =>
        Assert.Equal(installed, new FontCatalog(Registered).Contains(family));

    [Fact]
    public void CatalogListsNerdFontFamilies() =>
        Assert.Equal(
            ["Cascadia Code NF", "FiraCode Nerd Font Mono Retina", "JetBrainsMono NFM"],
            new FontCatalog(Registered).NerdFontFamilies());

    [Theory]
    [InlineData("Cascadia Code NF", true)]
    [InlineData("CaskaydiaCove Nerd Font", true)]
    [InlineData("JetBrainsMono NFP", true)]
    [InlineData("Cascadia Code", false)]
    [InlineData("Consolas", false)]
    [InlineData("Nerdy Mono", false)]
    [InlineData(null, false)]
    public void NerdFontNames(string? family, bool nerd) => Assert.Equal(nerd, NerdFonts.IsNerdFontName(family));

    [Fact]
    public void ServiceCountsTerminalsBundledFontsAndReadsTheRunningProfile()
    {
        var locations = WindowsTerminalLocations.FromLocalAppData(_root);
        var settings = locations.SettingsFiles[0];
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        File.WriteAllText(settings, """{ "profiles": { "defaults": { "font": { "face": "Hack NF, Cascadia Code NF" } } } }""");
        var env = new Dictionary<string, string?> { ["WT_SESSION"] = "1" };
        using var service = new WindowsFontService(() => Registered, locations, n => env.GetValueOrDefault(n), () => "Lucida Console", _ => []);

        Assert.True(service.IsInstalled("Cascadia Mono"));
        Assert.Equal("Hack NF, Cascadia Code NF", service.TerminalFont());
        Assert.True(service.TerminalHasNerdFont());

        File.WriteAllText(settings, """{ "profiles": { "defaults": { "font": { "face": "Hack NF" } } } }""");
        Assert.False(service.TerminalHasNerdFont());

        env.Remove("WT_SESSION");
        Assert.Equal("Lucida Console", service.TerminalFont());
        Assert.False(service.TerminalHasNerdFont());

        env["TERM_PROGRAM"] = "vscode";
        Assert.Null(service.TerminalFont());
        Assert.Null(service.TerminalHasNerdFont());
        env["TERM_PROGRAM"] = "WezTerm";
        Assert.True(service.TerminalHasNerdFont());
    }

    [Fact]
    public async Task DownloadReadsOnlyTheNeededPartsOfTheArchive()
    {
        var font = Encoding.ASCII.GetBytes(new string('F', 3_000_000));
        var spec = new FontFileSpec("ttf/Font.ttf", "Font.ttf", "Font Regular (TrueType)", Convert.ToHexString(SHA256.HashData(font)));
        var handler = new RangeHandler(Zip(("ttf/Other.ttf", new byte[5_000_000]), ("ttf/Font.ttf", font), ("ttf/Last.ttf", new byte[4_000_000])));
        using var http = new HttpClient(handler);

        var files = await FontDownload.ExtractAsync(http, new Uri("https://fonts.test/a.zip"), [spec], _root, null, CancellationToken.None);

        Assert.Equal(font, File.ReadAllBytes(Assert.Single(files)));
        Assert.True(handler.BytesServed < handler.Length / 2, $"{handler.BytesServed} of {handler.Length} bytes");
    }

    [Fact]
    public async Task DownloadFallsBackToTheWholeArchiveAndChecksTheHash()
    {
        var font = new byte[1000];
        var spec = new FontFileSpec("Font.ttf", "Font.ttf", "Font (TrueType)", new string('0', 64));
        using var http = new HttpClient(new RangeHandler(Zip(("Font.ttf", font))) { SupportsRanges = false });

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => FontDownload.ExtractAsync(http, new Uri("https://fonts.test/a.zip"), [spec], _root, null, CancellationToken.None));

        Assert.Contains("SHA-256", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_root, "Font.ttf")));
    }

    [Fact]
    public async Task ServiceInstallsThePinnedFiles()
    {
        var installedWith = new List<string>();
        using var http = new HttpClient(new RangeHandler(Zip(("x", new byte[1]))));
        using var service = new WindowsFontService(() => [], null, _ => null, () => null, files =>
        {
            installedWith.AddRange(files.Select(f => f.Spec.FileName));
            return [];
        }, http);

        var result = await service.InstallRecommendedAsync(null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("ttf/CascadiaCodeNF.ttf is not in", result.Message, StringComparison.Ordinal);
        Assert.Empty(installedWith);
        Assert.All(CascadiaCodeNerdFont.Files, f => Assert.Equal(64, f.Sha256.Length));
    }

    private static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                using var entry = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                entry.Write(data);
            }
        }

        return stream.ToArray();
    }

    /// <summary>Serves one file with (optional) single-range support, counting the bytes it sends.</summary>
    private sealed class RangeHandler(byte[] content) : HttpMessageHandler
    {
        public bool SupportsRanges { get; init; } = true;

        public long BytesServed { get; private set; }

        public long Length => content.Length;

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (SupportsRanges && request.Headers.Range?.Ranges.SingleOrDefault() is { From: { } from } range)
            {
                var to = Math.Min(range.To ?? content.Length - 1, content.Length - 1);
                var body = content.AsSpan((int)from, (int)(to - from + 1)).ToArray();
                BytesServed += body.Length;
                var partial = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(body), RequestMessage = request };
                partial.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, content.Length);
                return partial;
            }

            BytesServed += content.Length;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content), RequestMessage = request };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }
}
