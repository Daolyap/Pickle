using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Windows.Elevation;

internal sealed record HelperTimeouts(TimeSpan Connect, TimeSpan Handshake, TimeSpan Idle)
{
    public static HelperTimeouts Default { get; } = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2));
}

/// <summary>
/// Entry point for <c>pickle.exe --elevated-helper &lt;pipe&gt; &lt;nonce&gt;</c> (started with runas by
/// <see cref="ElevationBroker"/>). Connects to the broker's pipe, proves it knows the nonce, then runs allowlisted,
/// validated requests one at a time. Any protocol violation or rejected request ends the process.
/// </summary>
public static class ElevatedHelper
{
    public const int ExitOk = 0;
    public const int ExitError = 1;
    public const int ExitBadArguments = 2;
    public const int ExitProtocolViolation = 3;
    public const int ExitNotElevated = 5;

    public static int Run(string pipeName, string nonce, IPickleLogger log)
    {
        log.Info("elevation", $"helper started (pid {Environment.ProcessId})");
        if (!OperatingSystem.IsWindows())
        {
            log.Error("elevation", "the elevated helper only runs on Windows");
            return ExitBadArguments;
        }

        if (!ElevationProtocol.IsValidPipeName(pipeName) || !ElevationProtocol.IsValidNonce(nonce))
        {
            log.Error("elevation", "helper rejected: malformed pipe name or nonce");
            return ExitBadArguments;
        }

        if (!Environment.IsPrivilegedProcess)
        {
            log.Error("elevation", "helper rejected: the process is not elevated");
            return ExitNotElevated;
        }

        try
        {
            return RunWindowsAsync(pipeName, nonce, log).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.Error("elevation", "helper failed", ex);
            return ExitError;
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> RunWindowsAsync(string pipeName, string nonce, IPickleLogger log)
    {
        // Anonymous impersonation: the pipe server must not be able to act with this elevated token.
        using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            TokenImpersonationLevel.Anonymous);
        await pipe.ConnectAsync(HelperTimeouts.Default.Connect, CancellationToken.None).ConfigureAwait(false);
        var serverPid = PipeNative.TryGetServerProcessId(pipe, out var pid) ? pid.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?";
        log.Info("elevation", $"helper connected to {pipeName} (server pid {serverPid})");
        return await RunSessionAsync(pipe, nonce, new WindowsElevatedExecutor(log), log, HelperTimeouts.Default, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>The helper side of the protocol over any duplex stream (unit-tested with in-memory pipes).</summary>
    internal static async Task<int> RunSessionAsync(
        Stream pipe,
        string nonce,
        IElevatedExecutor executor,
        IPickleLogger log,
        HelperTimeouts timeouts,
        CancellationToken cancellationToken)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var writeLock = new SemaphoreSlim(1, 1);

        async Task SendAsync(ElevationMessage message)
        {
            await writeLock.WaitAsync(session.Token).ConfigureAwait(false);
            try
            {
                await ElevationProtocol.WriteAsync(pipe, message, session.Token).ConfigureAwait(false);
            }
            finally
            {
                writeLock.Release();
            }
        }

        try
        {
            await SendAsync(new ElevationMessage
            {
                Type = ElevationMessageType.Hello,
                Version = ElevationProtocol.Version,
                Nonce = nonce,
                ProcessId = Environment.ProcessId,
            }).ConfigureAwait(false);

            var ready = await ElevationProtocol.ReadAsync(pipe, timeouts.Handshake, session.Token).ConfigureAwait(false);
            if (ready.Type != ElevationMessageType.Ready)
            {
                return Violation(log, $"expected ready, got {ready.Type}");
            }

            for (var count = 0; ; count++)
            {
                var message = await ElevationProtocol.ReadAsync(pipe, timeouts.Idle, session.Token).ConfigureAwait(false);
                if (message.Type == ElevationMessageType.Bye)
                {
                    log.Info("elevation", $"helper finished after {count} request(s)");
                    return ExitOk;
                }

                if (message.Type != ElevationMessageType.Request || message.Kind is not { } kind || message.Arguments is not { } arguments || message.Id <= 0)
                {
                    return Violation(log, $"unexpected {message.Type} message");
                }

                if (count >= ElevationProtocol.MaxRequestsPerSession)
                {
                    return Violation(log, "too many requests in one session");
                }

                ValidatedOperation operation;
                try
                {
                    operation = ElevatedOperations.Validate(new ElevatedRequest(kind, arguments));
                }
                catch (ArgumentException ex)
                {
                    log.Error("elevation", $"helper rejected request {message.Id} ({kind}): {ex.Message}");
                    await SendAsync(new ElevationMessage
                    {
                        Type = ElevationMessageType.Response,
                        Id = message.Id,
                        Response = new ElevatedResponse(false, "Rejected by the elevated helper: " + ElevationProtocol.Truncate(ex.Message), 87),
                    }).ConfigureAwait(false);
                    return ExitProtocolViolation;
                }

                log.Info("elevation", $"helper executing {operation.Kind} [{string.Join(", ", operation.Ids)}]{(operation.All ? " --all" : string.Empty)}{(operation.Task is { } t ? " task " + t.Folder + "\\" + t.Name : string.Empty)}");
                var id = message.Id;
                var progress = new SyncProgress<string>(text =>
                {
                    try
                    {
                        SendAsync(new ElevationMessage { Type = ElevationMessageType.Progress, Id = id, Text = ElevationProtocol.Truncate(text) }).GetAwaiter().GetResult();
                    }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or ElevationProtocolException)
                    {
                        log.Warn("elevation", "helper lost the broker while reporting progress; cancelling", ex);
                        session.Cancel();
                    }
                });

                var response = await ElevatedOperations.ExecuteAsync(operation, executor, progress, session.Token).ConfigureAwait(false);
                log.Info("elevation", $"helper {operation.Kind} → success={response.Success} code={response.ExitCode}: {response.Message}");
                await SendAsync(new ElevationMessage
                {
                    Type = ElevationMessageType.Response,
                    Id = id,
                    Response = ElevationProtocol.Fit(id, response),
                }).ConfigureAwait(false);
            }
        }
        catch (ElevationProtocolException ex)
        {
            return Violation(log, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            log.Warn("elevation", "helper connection ended", ex);
            return ExitProtocolViolation;
        }
    }

    private static int Violation(IPickleLogger log, string message)
    {
        log.Error("elevation", "helper protocol violation: " + message);
        return ExitProtocolViolation;
    }
}
