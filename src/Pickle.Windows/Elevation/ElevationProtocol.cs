using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Pickle.Abstractions.Services;

namespace Pickle.Windows.Elevation;

internal enum ElevationMessageType
{
    /// <summary>Helper → broker, first message: protocol version, nonce, helper pid.</summary>
    Hello,

    /// <summary>Broker → helper: hello accepted.</summary>
    Ready,

    /// <summary>Broker → helper: run one allowlisted operation.</summary>
    Request,

    /// <summary>Helper → broker: a progress line for the running request.</summary>
    Progress,

    /// <summary>Helper → broker: the result of a request.</summary>
    Response,

    /// <summary>Broker → helper: no more requests; exit.</summary>
    Bye,
}

internal sealed record ElevationMessage
{
    public required ElevationMessageType Type { get; init; }

    public int Version { get; init; }

    public string? Nonce { get; init; }

    public int ProcessId { get; init; }

    public int Id { get; init; }

    public ElevatedOperationKind? Kind { get; init; }

    public IReadOnlyList<string>? Arguments { get; init; }

    public string? Text { get; init; }

    public ElevatedResponse? Response { get; init; }
}

/// <summary>Raised for anything that isn't exactly the expected protocol. Either side aborts the session on it.</summary>
internal sealed class ElevationProtocolException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Wire format between the broker (pipe server, unelevated) and the helper (pipe client, elevated):
/// a 4-byte little-endian length followed by that many bytes of UTF-8 JSON, at most <see cref="MaxMessageBytes"/>.
/// Deserialization is strict (no unknown members, no comments, enums by name only).
/// </summary>
internal static partial class ElevationProtocol
{
    public const int Version = 1;
    public const int MaxMessageBytes = 64 * 1024;
    public const int MaxProgressText = 2000;
    public const int MaxRequestsPerSession = 16;
    public const int NonceBytes = 32;
    public const string PipePrefix = "pickle-elev-";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 8,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    [GeneratedRegex("^pickle-elev-[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex PipeNameRegex();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex NonceRegex();

    public static string NewPipeName() => PipePrefix + Guid.NewGuid().ToString("N");

    public static string NewNonce() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(NonceBytes));

    public static bool IsValidPipeName(string? name) => name is not null && PipeNameRegex().IsMatch(name);

    public static bool IsValidNonce(string? nonce) => nonce is not null && NonceRegex().IsMatch(nonce);

    /// <summary>Constant-time comparison of two nonces (length is not secret).</summary>
    public static bool NonceEquals(string? expected, string? actual)
    {
        if (expected is null || actual is null)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(actual));
    }

    public static byte[] Encode(ElevationMessage message)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, Options);
        if (payload.Length > MaxMessageBytes)
        {
            throw new ElevationProtocolException($"Message of {payload.Length} bytes exceeds the {MaxMessageBytes}-byte limit.");
        }

        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }

    public static ElevationMessage Decode(ReadOnlySpan<byte> payload)
    {
        ElevationMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<ElevationMessage>(payload, Options);
        }
        catch (JsonException ex)
        {
            throw new ElevationProtocolException("Malformed message: " + ex.Message, ex);
        }

        if (message is null || !Enum.IsDefined(message.Type))
        {
            throw new ElevationProtocolException("Malformed message: missing type.");
        }

        return message;
    }

    public static async Task WriteAsync(Stream stream, ElevationMessage message, CancellationToken cancellationToken)
    {
        var frame = Encode(message);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one frame. Timeouts, EOF, oversize frames and malformed JSON all become <see cref="ElevationProtocolException"/>.</summary>
    public static async Task<ElevationMessage> ReadAsync(Stream stream, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            var header = new byte[4];
            await stream.ReadExactlyAsync(header, cts.Token).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length <= 0 || length > MaxMessageBytes)
            {
                throw new ElevationProtocolException($"Invalid frame length {length}.");
            }

            var payload = new byte[length];
            await stream.ReadExactlyAsync(payload, cts.Token).ConfigureAwait(false);
            return Decode(payload);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ElevationProtocolException($"Timed out after {timeout.TotalSeconds:0}s waiting for the other side.");
        }
        catch (EndOfStreamException ex)
        {
            throw new ElevationProtocolException("The other side closed the connection.", ex);
        }
        catch (IOException ex)
        {
            throw new ElevationProtocolException("The connection failed: " + ex.Message, ex);
        }
    }

    public static string Truncate(string text) => text.Length <= MaxProgressText ? text : text[..MaxProgressText] + "…";
}
