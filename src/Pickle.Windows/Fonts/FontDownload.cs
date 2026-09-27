using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Pickle.Windows.Fonts;

/// <summary>One font file inside a release archive, pinned by its SHA-256.</summary>
public sealed record FontFileSpec(string EntryName, string FileName, string RegistryName, string Sha256);

/// <summary>The Nerd Font variant of Cascadia Code (SIL OFL 1.1) from Microsoft's GitHub release.</summary>
public static class CascadiaCodeNerdFont
{
    public const string Family = "Cascadia Code NF";

    public const string Version = "2407.24";

    public static readonly Uri Archive = new($"https://github.com/microsoft/cascadia-code/releases/download/v{Version}/CascadiaCode-{Version}.zip");

    public static readonly IReadOnlyList<FontFileSpec> Files =
    [
        new("ttf/CascadiaCodeNF.ttf", "CascadiaCodeNF.ttf", "Cascadia Code NF Regular (TrueType)", "16d00fae9fc289cda8c9ee4c578ccb4d410f3fa54ec0e5a3901c059b0248a970"),
        new("ttf/CascadiaCodeNFItalic.ttf", "CascadiaCodeNFItalic.ttf", "Cascadia Code NF Italic (TrueType)", "95fb04b5aaae44c4cb698cfd1b62bddc44e899845f3c494c342d2b0bbf19572e"),
    ];
}

/// <summary>
/// Extracts pinned files from a zip on a web server. The Cascadia release zip is ~150 MB of every variant, stored
/// uncompressed, so the central directory and the wanted entries are read with HTTP range requests (a few MB); a
/// server that ignores ranges gets the whole archive downloaded to a temporary file instead.
/// </summary>
public static class FontDownload
{
    public static async Task<IReadOnlyList<string>> ExtractAsync(
        HttpClient http, Uri archive, IReadOnlyList<FontFileSpec> files, string targetDirectory, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(targetDirectory);
        progress?.Report($"Downloading {archive.Host}{archive.AbsolutePath}");
        await using var stream = await HttpRangeStream.OpenAsync(http, archive, cancellationToken).ConfigureAwait(false) as Stream
            ?? await DownloadWholeAsync(http, archive, progress, cancellationToken).ConfigureAwait(false);

        return await Task.Run(
            () =>
            {
                using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
                var written = new List<string>();
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = zip.GetEntry(file.EntryName) ?? throw new InvalidDataException($"{file.EntryName} is not in {archive}.");
                    var target = Path.Combine(targetDirectory, file.FileName);
                    progress?.Report($"Extracting {file.FileName} ({entry.Length / 1024:N0} KB)");
                    using (var input = entry.Open())
                    using (var output = File.Create(target))
                    {
                        input.CopyTo(output);
                    }

                    VerifySha256(target, file.Sha256);
                    written.Add(target);
                }

                return (IReadOnlyList<string>)written;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public static void VerifySha256(string path, string expected)
    {
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            stream.Dispose();
            File.Delete(path);
            throw new InvalidDataException($"{Path.GetFileName(path)} failed its SHA-256 check (expected {expected}, got {actual.ToLowerInvariant()}).");
        }
    }

    private static async Task<Stream> DownloadWholeAsync(HttpClient http, Uri archive, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        progress?.Report("The server does not support partial downloads; downloading the whole archive");
        var temp = Path.Combine(Path.GetTempPath(), "pickle-font-" + Guid.NewGuid().ToString("N") + ".zip");
        var file = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
        try
        {
            using var response = await http.GetAsync(archive, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await body.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            file.Position = 0;
            return file;
        }
        catch
        {
            await file.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>A read-only, seekable view of a remote file that fetches 1 MB blocks with HTTP range requests.</summary>
public sealed class HttpRangeStream : Stream
{
    private const int BlockSize = 1 << 20;
    private readonly HttpClient _http;
    private readonly Uri _uri;
    private readonly Dictionary<long, byte[]> _blocks = [];
    private long _position;

    private HttpRangeStream(HttpClient http, Uri uri, long length)
    {
        _http = http;
        _uri = uri;
        Length = length;
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length { get; }

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, Length);
    }

    /// <summary>Null when the server does not answer a range request with 206 and a total length.</summary>
    public static async Task<HttpRangeStream?> OpenAsync(HttpClient http, Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.PartialContent || response.Content.Headers.ContentRange?.Length is not { } length || length <= 0)
        {
            return null;
        }

        // Release downloads redirect to a signed storage URL; asking it directly saves a redirect per block.
        return new HttpRangeStream(http, response.RequestMessage?.RequestUri ?? uri, length);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var total = 0;
        while (buffer.Length > 0 && _position < Length)
        {
            var index = _position / BlockSize;
            var block = Block(index);
            var within = (int)(_position - (index * BlockSize));
            var n = Math.Min(buffer.Length, block.Length - within);
            block.AsSpan(within, n).CopyTo(buffer);
            buffer = buffer[n..];
            _position += n;
            total += n;
        }

        return total;
    }

    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => _position + offset,
        _ => Length + offset,
    };

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private byte[] Block(long index)
    {
        if (_blocks.TryGetValue(index, out var cached))
        {
            return cached;
        }

        var start = index * BlockSize;
        var end = Math.Min(start + BlockSize, Length) - 1;
        using var request = new HttpRequestMessage(HttpMethod.Get, _uri);
        request.Headers.Range = new RangeHeaderValue(start, end);
        using var response = _http.Send(request);
        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            throw new IOException($"Range request for bytes {start}-{end} returned {(int)response.StatusCode}.");
        }

        using var body = response.Content.ReadAsStream();
        var block = new byte[end - start + 1];
        body.ReadExactly(block);
        _blocks[index] = block;
        return block;
    }
}
