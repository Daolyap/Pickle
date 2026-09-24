using Pickle.Abstractions.Services;
using Pickle.Windows.Elevation;

namespace Pickle.Windows.Tests.Elevation;

public class ElevationSessionTests
{
    private static readonly HelperTimeouts Fast = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task BrokerAndHelperRunABatch()
    {
        var (server, client) = DuplexStream.CreatePair();
        var nonce = ElevationProtocol.NewNonce();
        var executor = new FakeExecutor();
        var log = new ListLogger();
        var progress = new List<string>();

        var helper = Task.Run(() => ElevatedHelper.RunSessionAsync(client, nonce, executor, log, Fast, CancellationToken.None));
        var responses = await ElevationBroker.RunClientSessionAsync(
            server,
            nonce,
            [
                new ElevatedRequest(ElevatedOperationKind.WingetUpgrade, ["Git.Git"]),
                new ElevatedRequest(ElevatedOperationKind.WindowsUpdateInstall, ["0A1B2C3D-4E5F-6071-8293-A4B5C6D7E8F9"]),
            ],
            new SyncProgress<string>(progress.Add),
            log,
            Wait,
            CancellationToken.None).WaitAsync(Wait);

        Assert.Equal(ElevatedHelper.ExitOk, await helper.WaitAsync(Wait));
        Assert.Equal(2, responses.Count);
        Assert.All(responses, r => Assert.True(r.Success));
        Assert.Equal(["winget upgrade --id Git.Git --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity", "wu 0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9"], executor.Calls);
        Assert.Contains(progress, p => p.StartsWith("Upgrading Git.Git", StringComparison.Ordinal));
        Assert.Contains(log.Lines, l => l.Contains("helper executing WingetUpgrade", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BrokerRejectsAHelperWithTheWrongNonce()
    {
        var (server, client) = DuplexStream.CreatePair();
        var executor = new FakeExecutor();
        var helper = Task.Run(() => ElevatedHelper.RunSessionAsync(client, ElevationProtocol.NewNonce(), executor, new ListLogger(), Fast, CancellationToken.None));

        await Assert.ThrowsAsync<ElevationProtocolException>(() => ElevationBroker.RunClientSessionAsync(
            server, ElevationProtocol.NewNonce(), [new ElevatedRequest(ElevatedOperationKind.WingetRepairSource, [])], null, new ListLogger(), Wait, CancellationToken.None));
        server.Dispose();

        Assert.Equal(ElevatedHelper.ExitProtocolViolation, await helper.WaitAsync(Wait));
        Assert.Empty(executor.Calls);
    }

    [Fact]
    public async Task HelperRejectsInvalidRequestsAndExits()
    {
        var (server, client) = DuplexStream.CreatePair();
        var nonce = ElevationProtocol.NewNonce();
        var executor = new FakeExecutor();
        var helper = Task.Run(() => ElevatedHelper.RunSessionAsync(client, nonce, executor, new ListLogger(), Fast, CancellationToken.None));

        // A hostile broker skips client-side validation and sends an injection attempt.
        await ExpectHelloAsync(server, nonce);
        await ElevationProtocol.WriteAsync(server, new ElevationMessage { Type = ElevationMessageType.Ready }, CancellationToken.None);
        await ElevationProtocol.WriteAsync(server, new ElevationMessage
        {
            Type = ElevationMessageType.Request,
            Id = 1,
            Kind = ElevatedOperationKind.WingetInstall,
            Arguments = ["Git.Git --override \"/SILENT & calc.exe\""],
        }, CancellationToken.None);

        var response = await ElevationProtocol.ReadAsync(server, Wait, CancellationToken.None);
        Assert.Equal(ElevationMessageType.Response, response.Type);
        Assert.False(response.Response?.Success);
        Assert.Contains("Rejected", response.Response?.Message, StringComparison.Ordinal);
        Assert.Equal(ElevatedHelper.ExitProtocolViolation, await helper.WaitAsync(Wait));
        Assert.Empty(executor.Calls);
    }

    [Fact]
    public async Task HelperExitsOnUnexpectedMessages()
    {
        foreach (var bad in new[]
        {
            new ElevationMessage { Type = ElevationMessageType.Request, Id = 1, Kind = ElevatedOperationKind.WingetRepairSource, Arguments = [] },
            new ElevationMessage { Type = ElevationMessageType.Response },
        })
        {
            var (server, client) = DuplexStream.CreatePair();
            var nonce = ElevationProtocol.NewNonce();
            var executor = new FakeExecutor();
            var helper = Task.Run(() => ElevatedHelper.RunSessionAsync(client, nonce, executor, new ListLogger(), Fast, CancellationToken.None));
            await ExpectHelloAsync(server, nonce);

            // The first message must be "ready"; anything else ends the helper.
            await ElevationProtocol.WriteAsync(server, bad, CancellationToken.None);
            Assert.Equal(ElevatedHelper.ExitProtocolViolation, await helper.WaitAsync(Wait));
            Assert.Empty(executor.Calls);
        }
    }

    [Fact]
    public async Task HelperExitsOnGarbageFramesAndMissingArguments()
    {
        var (server, client) = DuplexStream.CreatePair();
        var nonce = ElevationProtocol.NewNonce();
        var executor = new FakeExecutor();
        var helper = Task.Run(() => ElevatedHelper.RunSessionAsync(client, nonce, executor, new ListLogger(), Fast, CancellationToken.None));
        await ExpectHelloAsync(server, nonce);
        await ElevationProtocol.WriteAsync(server, new ElevationMessage { Type = ElevationMessageType.Ready }, CancellationToken.None);
        await ElevationProtocol.WriteAsync(server, new ElevationMessage { Type = ElevationMessageType.Request, Id = 1, Kind = ElevatedOperationKind.WingetRepairSource }, CancellationToken.None);
        Assert.Equal(ElevatedHelper.ExitProtocolViolation, await helper.WaitAsync(Wait));

        (server, client) = DuplexStream.CreatePair();
        helper = Task.Run(() => ElevatedHelper.RunSessionAsync(client, nonce, executor, new ListLogger(), Fast, CancellationToken.None));
        await ExpectHelloAsync(server, nonce);
        await server.WriteAsync(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F, 1, 2, 3 });
        await server.FlushAsync();
        Assert.Equal(ElevatedHelper.ExitProtocolViolation, await helper.WaitAsync(Wait));
        Assert.Empty(executor.Calls);
    }

    [Fact]
    public async Task HelperTimesOutWaitingForTheBroker()
    {
        var (server, client) = DuplexStream.CreatePair();
        var timeouts = new HelperTimeouts(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200));
        var result = await ElevatedHelper.RunSessionAsync(client, ElevationProtocol.NewNonce(), new FakeExecutor(), new ListLogger(), timeouts, CancellationToken.None).WaitAsync(Wait);
        Assert.Equal(ElevatedHelper.ExitProtocolViolation, result);
        server.Dispose();
    }

    [Fact]
    public async Task BrokerFillsFailuresWhenTheHelperDisappears()
    {
        var (server, client) = DuplexStream.CreatePair();
        var nonce = ElevationProtocol.NewNonce();
        var fakeHelper = Task.Run(async () =>
        {
            await ElevationProtocol.WriteAsync(client, new ElevationMessage { Type = ElevationMessageType.Hello, Version = ElevationProtocol.Version, Nonce = nonce }, CancellationToken.None);
            await ElevationProtocol.ReadAsync(client, Wait, CancellationToken.None);
            await ElevationProtocol.ReadAsync(client, Wait, CancellationToken.None);
            client.Dispose();
        });

        var responses = await ElevationBroker.RunClientSessionAsync(
            server,
            nonce,
            [new ElevatedRequest(ElevatedOperationKind.WingetRepairSource, []), new ElevatedRequest(ElevatedOperationKind.WingetUpgrade, ["--all"])],
            null,
            new ListLogger(),
            Wait,
            CancellationToken.None).WaitAsync(Wait);
        await fakeHelper;

        Assert.Equal(2, responses.Count);
        Assert.All(responses, r => Assert.False(r.Success));
    }

    [Fact]
    public async Task BrokerValidatesBeforeElevatingAndRunsInProcessWhenElevated()
    {
        var executor = new FakeExecutor();
        var broker = new ElevationBroker(new ListLogger(), isSupported: true, isElevated: true, () => executor);

        await Assert.ThrowsAsync<ArgumentException>(() => broker.RunAsync([new ElevatedRequest(ElevatedOperationKind.WingetUpgrade, ["bad id"])]));
        Assert.Empty(executor.Calls);

        var responses = await broker.RunAsync([new ElevatedRequest(ElevatedOperationKind.WingetUpgrade, ["--all"])]);
        Assert.True(Assert.Single(responses).Success);
        Assert.Equal("winget upgrade --all --silent --accept-package-agreements --accept-source-agreements --disable-interactivity", Assert.Single(executor.Calls));

        var unsupported = new ElevationBroker(new ListLogger(), isSupported: false, isElevated: false, () => executor);
        Assert.False(Assert.Single(await unsupported.RunAsync([new ElevatedRequest(ElevatedOperationKind.WingetRepairSource, [])])).Success);
    }

    [Fact]
    public void HelperEntryPointRejectsBadArguments()
    {
        var log = new ListLogger();
        Assert.NotEqual(ElevatedHelper.ExitOk, ElevatedHelper.Run("some-other-pipe", ElevationProtocol.NewNonce(), log));
        Assert.NotEqual(ElevatedHelper.ExitOk, ElevatedHelper.Run(ElevationProtocol.NewPipeName(), "short", log));
    }

    private static async Task ExpectHelloAsync(Stream server, string nonce)
    {
        var hello = await ElevationProtocol.ReadAsync(server, Wait, CancellationToken.None);
        Assert.Equal(ElevationMessageType.Hello, hello.Type);
        Assert.Equal(nonce, hello.Nonce);
    }
}
