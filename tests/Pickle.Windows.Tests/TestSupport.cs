using System.IO.Pipelines;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Windows.Elevation;
using Pickle.Windows.Processes;

namespace Pickle.Windows.Tests;

internal sealed class ListLogger : IPickleLogger
{
    public List<string> Lines { get; } = [];

    public void Log(PickleLogLevel level, string category, string message, Exception? exception = null)
    {
        lock (Lines)
        {
            Lines.Add($"{level} {category}: {message}");
        }
    }
}

/// <summary>An <see cref="IPickleShell"/> whose background invocations return nothing (so winget falls back to the CLI).</summary>
internal sealed class NullShell : IPickleShell
{
    public List<string> Scripts { get; } = [];

    public string CurrentDirectory => "/";

    public bool IsInteractive => false;

    public Task<ShellResult> InvokeAsync(string script, IReadOnlyDictionary<string, object?>? parameters = null, ShellTarget target = ShellTarget.Main, CancellationToken cancellationToken = default)
    {
        Scripts.Add(script);
        return Task.FromResult(ShellResult.Empty);
    }

    public void InsertText(string text)
    {
    }

    public void ReplaceInput(string text)
    {
    }

    public void SubmitCommand(string commandLine)
    {
    }

    public void SetLocation(string path)
    {
    }

    public void WriteLine(string text)
    {
    }
}

/// <summary>Records process launches and replies with canned output keyed by the first argument.</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    public List<(string File, IReadOnlyList<string> Args)> Calls { get; } = [];

    public Dictionary<string, ProcessResult> Replies { get; } = [];

    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, Action<string>? onSegment, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Calls.Add((fileName, arguments.ToList()));
        var key = arguments.Count > 0 ? arguments[0] : string.Empty;
        var reply = Replies.TryGetValue(key, out var r) ? r : new ProcessResult(0, string.Empty);
        if (onSegment is not null)
        {
            foreach (var segment in reply.Output.Split('\r', '\n', StringSplitOptions.RemoveEmptyEntries))
            {
                onSegment(segment);
            }
        }

        return Task.FromResult(reply);
    }
}

internal sealed class FakeExecutor : IElevatedExecutor
{
    public List<string> Calls { get; } = [];

    public Exception? Throw { get; set; }

    public Task<ElevatedResponse> RepairWingetSourceAsync(IProgress<string> progress, CancellationToken cancellationToken) => Record("repair");

    public Task<ElevatedResponse> RunWingetAsync(IReadOnlyList<string> arguments, IProgress<string> progress, CancellationToken cancellationToken)
    {
        progress.Report("working on " + string.Join(' ', arguments.Take(3)));
        return Record("winget " + string.Join(' ', arguments));
    }

    public Task<ElevatedResponse> InstallWindowsUpdatesAsync(IReadOnlyList<string> updateIds, IProgress<string> progress, CancellationToken cancellationToken) =>
        Record("wu " + string.Join(',', updateIds));

    public Task<ElevatedResponse> RegisterTaskAsync(ScheduledTaskDefinition definition, IProgress<string> progress, CancellationToken cancellationToken) =>
        Record($"task {definition.Folder}\\{definition.Name} elevated={definition.RunElevated}");

    private Task<ElevatedResponse> Record(string call)
    {
        lock (Calls)
        {
            Calls.Add(call);
        }

        return Throw is { } ex ? Task.FromException<ElevatedResponse>(ex) : Task.FromResult(new ElevatedResponse(true, call + " ok"));
    }
}

/// <summary>Two connected in-memory duplex streams (like the two ends of a named pipe).</summary>
internal sealed class DuplexStream(Stream reader, Stream writer) : Stream
{
    public static (Stream Server, Stream Client) CreatePair()
    {
        var toClient = new Pipe();
        var toServer = new Pipe();
        return (new DuplexStream(toServer.Reader.AsStream(), toClient.Writer.AsStream()),
                new DuplexStream(toClient.Reader.AsStream(), toServer.Writer.AsStream()));
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() => writer.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => writer.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => reader.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => reader.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => reader.ReadAsync(buffer, offset, count, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) => writer.Write(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => writer.WriteAsync(buffer, cancellationToken);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => writer.WriteAsync(buffer, offset, count, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            writer.Dispose();
            reader.Dispose();
        }

        base.Dispose(disposing);
    }
}
