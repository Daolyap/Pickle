using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Windows.Elevation;

/// <summary>
/// Runs allowlisted privileged operations. In an elevated Pickle they run in-process; otherwise one elevated helper
/// (<c>pickle.exe --elevated-helper</c>, one UAC prompt) is started per batch and driven over a per-user named pipe
/// authenticated with a random nonce and the helper's process id.
/// </summary>
public sealed class ElevationBroker : IElevationBroker
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);

    private readonly IPickleLogger _log;
    private readonly Func<IElevatedExecutor> _inProcessExecutor;

    public ElevationBroker(IPickleLogger log)
        : this(log, OperatingSystem.IsWindows(), OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess, () => CreateWindowsExecutor(log))
    {
    }

    internal ElevationBroker(IPickleLogger log, bool isSupported, bool isElevated, Func<IElevatedExecutor> inProcessExecutor)
    {
        _log = log;
        IsSupported = isSupported;
        IsElevated = isElevated;
        _inProcessExecutor = inProcessExecutor;
    }

    public bool IsSupported { get; }

    public bool IsElevated { get; }

    public async Task<IReadOnlyList<ElevatedResponse>> RunAsync(IReadOnlyList<ElevatedRequest> batch, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
        {
            return [];
        }

        if (batch.Count > ElevationProtocol.MaxRequestsPerSession)
        {
            throw new ArgumentException($"At most {ElevationProtocol.MaxRequestsPerSession} operations per elevation.");
        }

        // Validate before any UAC prompt so bad input never reaches the helper.
        var operations = batch.Select(ElevatedOperations.Validate).ToList();
        _log.Info("elevation", "batch: " + string.Join("; ", operations.Select(Describe)));

        if (!IsSupported)
        {
            return [.. batch.Select(_ => new ElevatedResponse(false, "Elevation is only available on Windows.", 1))];
        }

        if (IsElevated)
        {
            var executor = _inProcessExecutor();
            var sink = progress ?? new SyncProgress<string>(_ => { });
            var responses = new List<ElevatedResponse>();
            foreach (var operation in operations)
            {
                responses.Add(await ElevatedOperations.ExecuteAsync(operation, executor, sink, cancellationToken).ConfigureAwait(false));
            }

            return responses;
        }

        if (!OperatingSystem.IsWindows())
        {
            return [.. batch.Select(_ => new ElevatedResponse(false, "Elevation is only available on Windows.", 1))];
        }

        return await RunViaHelperAsync(batch, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The broker side of the protocol over any duplex stream (unit-tested with in-memory pipes).</summary>
    internal static async Task<IReadOnlyList<ElevatedResponse>> RunClientSessionAsync(
        Stream pipe,
        string nonce,
        IReadOnlyList<ElevatedRequest> batch,
        IProgress<string>? progress,
        IPickleLogger log,
        TimeSpan handshakeTimeout,
        CancellationToken cancellationToken)
    {
        var hello = await ElevationProtocol.ReadAsync(pipe, handshakeTimeout, cancellationToken).ConfigureAwait(false);
        if (hello.Type != ElevationMessageType.Hello || hello.Version != ElevationProtocol.Version || !ElevationProtocol.NonceEquals(nonce, hello.Nonce))
        {
            log.Error("elevation", $"helper failed authentication (type {hello.Type}, version {hello.Version})");
            throw new ElevationProtocolException("The elevated helper failed authentication.");
        }

        await ElevationProtocol.WriteAsync(pipe, new ElevationMessage { Type = ElevationMessageType.Ready }, cancellationToken).ConfigureAwait(false);
        var responses = new List<ElevatedResponse>();
        for (var i = 0; i < batch.Count; i++)
        {
            var request = batch[i];
            var id = i + 1;
            try
            {
                await ElevationProtocol.WriteAsync(
                    pipe,
                    new ElevationMessage { Type = ElevationMessageType.Request, Id = id, Kind = request.Kind, Arguments = request.Arguments },
                    cancellationToken).ConfigureAwait(false);
                var timeout = ElevatedOperations.TimeoutFor(request.Kind) + TimeSpan.FromMinutes(1);
                while (true)
                {
                    var message = await ElevationProtocol.ReadAsync(pipe, timeout, cancellationToken).ConfigureAwait(false);
                    if (message.Type == ElevationMessageType.Progress && message.Id == id)
                    {
                        if (message.Text is { } text)
                        {
                            progress?.Report(ElevationProtocol.Truncate(text));
                        }

                        continue;
                    }

                    if (message.Type == ElevationMessageType.Response && message.Id == id && message.Response is { } response)
                    {
                        log.Info("elevation", $"{request.Kind} → success={response.Success}: {response.Message}");
                        responses.Add(response);
                        break;
                    }

                    throw new ElevationProtocolException($"Unexpected {message.Type} message from the elevated helper.");
                }
            }
            catch (Exception ex) when (ex is ElevationProtocolException or IOException)
            {
                log.Error("elevation", $"session ended during {request.Kind}", ex);
                while (responses.Count < batch.Count)
                {
                    responses.Add(new ElevatedResponse(false, "The elevated helper stopped: " + ex.Message, 1));
                }

                return responses;
            }
        }

        try
        {
            await ElevationProtocol.WriteAsync(pipe, new ElevationMessage { Type = ElevationMessageType.Bye }, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            log.Warn("elevation", "could not say goodbye to the helper", ex);
        }

        return responses;
    }

    internal static string Describe(ValidatedOperation operation) => operation.Kind switch
    {
        ElevatedOperationKind.TaskRegisterElevated => $"{operation.Kind} {operation.Task?.Folder}\\{operation.Task?.Name}",
        _ when operation.All => $"{operation.Kind} --all",
        _ => $"{operation.Kind} [{string.Join(", ", operation.Ids)}]",
    };

    private static IElevatedExecutor CreateWindowsExecutor(IPickleLogger log)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Elevated operations are only available on Windows.");
        }

        return new WindowsElevatedExecutor(log);
    }

    [SupportedOSPlatform("windows")]
    private async Task<IReadOnlyList<ElevatedResponse>> RunViaHelperAsync(IReadOnlyList<ElevatedRequest> batch, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate pickle.exe to start the elevated helper.");
        var pipeName = ElevationProtocol.NewPipeName();
        var nonce = ElevationProtocol.NewNonce();
        using var server = PipeNative.CreateServer(pipeName);

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Environment.SystemDirectory,
        };
        psi.ArgumentList.Add("--elevated-helper");
        psi.ArgumentList.Add(pipeName);
        psi.ArgumentList.Add(nonce);

        Process? helper;
        try
        {
            progress?.Report("Waiting for the administrator (UAC) prompt…");
            _log.Info("elevation", $"starting elevated helper on {pipeName}");
            helper = Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            _log.Info("elevation", "the UAC prompt was declined");
            throw new OperationCanceledException("The administrator (UAC) prompt was declined.", ex);
        }

        if (helper is null)
        {
            throw new InvalidOperationException("The elevated helper did not start.");
        }

        using (helper)
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(ConnectTimeout);
            var connect = server.WaitForConnectionAsync(connectCts.Token);
            var exited = helper.WaitForExitAsync(connectCts.Token);
            var first = await Task.WhenAny(connect, exited).ConfigureAwait(false);
            if (first != connect || !connect.IsCompletedSuccessfully)
            {
                await connectCts.CancelAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var reason = helper.HasExited
                    ? $"The elevated helper exited before connecting (code {helper.ExitCode}). See the Pickle log."
                    : "Timed out waiting for the elevated helper.";
                _log.Error("elevation", reason);
                throw new InvalidOperationException(reason);
            }

            if (!PipeNative.TryGetClientProcessId(server, out var clientPid) || clientPid != helper.Id)
            {
                _log.Error("elevation", $"unexpected pipe client (pid {clientPid}, expected {helper.Id}); aborting");
                throw new InvalidOperationException("An unexpected process connected to the elevation pipe; aborted.");
            }

            progress?.Report("Elevated helper connected.");
            IReadOnlyList<ElevatedResponse> responses;
            try
            {
                responses = await RunClientSessionAsync(server, nonce, batch, progress, _log, HandshakeTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (ElevationProtocolException ex)
            {
                throw new InvalidOperationException(ex.Message, ex);
            }

            try
            {
                await helper.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
                _log.Info("elevation", $"helper exited with code {helper.ExitCode}");
            }
            catch (TimeoutException)
            {
                _log.Warn("elevation", "the elevated helper did not exit in time");
            }

            return responses;
        }
    }
}
