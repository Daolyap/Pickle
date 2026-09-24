using System.Buffers.Binary;
using System.Text;
using Pickle.Abstractions.Services;
using Pickle.Windows.Elevation;

namespace Pickle.Windows.Tests.Elevation;

public class ElevationProtocolTests
{
    private static readonly string Nonce = new('a', 64);

    [Fact]
    public async Task FramesRoundTrip()
    {
        using var stream = new MemoryStream();
        var message = new ElevationMessage
        {
            Type = ElevationMessageType.Request,
            Id = 3,
            Kind = ElevatedOperationKind.WingetUpgrade,
            Arguments = ["Git.Git"],
        };
        await ElevationProtocol.WriteAsync(stream, message, CancellationToken.None);
        stream.Position = 0;

        var read = await ElevationProtocol.ReadAsync(stream, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(ElevationMessageType.Request, read.Type);
        Assert.Equal(3, read.Id);
        Assert.Equal(ElevatedOperationKind.WingetUpgrade, read.Kind);
        Assert.Equal(["Git.Git"], read.Arguments);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(ElevationProtocol.MaxMessageBytes + 1)]
    public async Task RejectsBadFrameLengths(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using var stream = new MemoryStream([.. header, .. new byte[16]]);
        await Assert.ThrowsAsync<ElevationProtocolException>(() => ElevationProtocol.ReadAsync(stream, TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    [Theory]
    [InlineData("{\"type\":\"request\",\"id\":1,\"kind\":\"wingetUpgrade\",\"arguments\":[\"Git.Git\"],\"command\":\"calc.exe\"}")]
    [InlineData("{\"type\":\"request\",\"id\":1,\"kind\":1,\"arguments\":[\"Git.Git\"]}")]
    [InlineData("{\"type\":\"request\",\"id\":1,\"kind\":\"runAnything\",\"arguments\":[]}")]
    [InlineData("{\"type\":\"launchMissiles\"}")]
    [InlineData("{\"id\":1}")]
    [InlineData("{\"type\":\"hello\", /* comment */ \"version\":1}")]
    [InlineData("not json")]
    [InlineData("null")]
    public void RejectsMalformedOrUnknownMessages(string json) =>
        Assert.Throws<ElevationProtocolException>(() => ElevationProtocol.Decode(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public async Task TruncatedFrameIsAProtocolError()
    {
        var frame = ElevationProtocol.Encode(new ElevationMessage { Type = ElevationMessageType.Bye });
        using var stream = new MemoryStream(frame[..^2]);
        await Assert.ThrowsAsync<ElevationProtocolException>(() => ElevationProtocol.ReadAsync(stream, TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    [Fact]
    public async Task ReadTimesOut()
    {
        var (server, _) = DuplexStream.CreatePair();
        using (server)
        {
            var ex = await Assert.ThrowsAsync<ElevationProtocolException>(() => ElevationProtocol.ReadAsync(server, TimeSpan.FromMilliseconds(100), CancellationToken.None));
            Assert.Contains("Timed out", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void OversizedMessagesAreNotEncoded() =>
        Assert.Throws<ElevationProtocolException>(() => ElevationProtocol.Encode(new ElevationMessage
        {
            Type = ElevationMessageType.Progress,
            Text = new string('x', ElevationProtocol.MaxMessageBytes),
        }));

    [Fact]
    public void NoncesAndPipeNamesAreStrict()
    {
        var nonce = ElevationProtocol.NewNonce();
        Assert.True(ElevationProtocol.IsValidNonce(nonce));
        Assert.NotEqual(nonce, ElevationProtocol.NewNonce());
        Assert.False(ElevationProtocol.IsValidNonce(nonce.ToUpperInvariant()));
        Assert.False(ElevationProtocol.IsValidNonce(nonce[..^1]));
        Assert.False(ElevationProtocol.IsValidNonce(null));

        Assert.True(ElevationProtocol.IsValidPipeName(ElevationProtocol.NewPipeName()));
        Assert.False(ElevationProtocol.IsValidPipeName(@"pickle-elev-..\..\pipe\x"));
        Assert.False(ElevationProtocol.IsValidPipeName("other-pipe"));

        Assert.True(ElevationProtocol.NonceEquals(Nonce, new string('a', 64)));
        Assert.False(ElevationProtocol.NonceEquals(Nonce, new string('b', 64)));
        Assert.False(ElevationProtocol.NonceEquals(Nonce, Nonce[..32]));
        Assert.False(ElevationProtocol.NonceEquals(Nonce, null));
    }
}
