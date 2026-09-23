using System.Globalization;
using System.Text;
using Pickle.Abstractions;

namespace Pickle.Core.Logging;

/// <summary>
/// Appends structured lines to logs/pickle-YYYYMMDD.log. Level comes from --log-level or PICKLE_TRACE=1 (trace).
/// Never writes to the terminal.
/// </summary>
public sealed class FileLogger : IPickleLogger, IDisposable
{
    private readonly object _gate = new();
    private readonly PickleLogLevel _minimum;
    private readonly string? _directory;
    private StreamWriter? _writer;

    public FileLogger(string? directory, PickleLogLevel minimum)
    {
        _directory = directory;
        _minimum = minimum;
    }

    public static PickleLogLevel ResolveLevel(string? cliLevel)
    {
        if (Environment.GetEnvironmentVariable("PICKLE_TRACE") is "1" or "true")
        {
            return PickleLogLevel.Trace;
        }

        return Enum.TryParse<PickleLogLevel>(cliLevel, ignoreCase: true, out var level) ? level : PickleLogLevel.Warning;
    }

    public void Log(PickleLogLevel level, string category, string message, Exception? exception = null)
    {
        if (level < _minimum || _directory is null)
        {
            return;
        }

        var line = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture))
            .Append(' ').Append(level.ToString().ToUpperInvariant().PadRight(7))
            .Append(" [").Append(category).Append("] ")
            .Append(message);
        if (exception is not null)
        {
            line.Append(" | ").Append(exception.GetType().Name).Append(": ").Append(exception.Message);
            if (level >= PickleLogLevel.Error)
            {
                line.AppendLine().Append(exception.StackTrace);
            }
        }

        lock (_gate)
        {
            try
            {
                _writer ??= Open();
                _writer.WriteLine(line.ToString());
                _writer.Flush();
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private StreamWriter Open()
    {
        Directory.CreateDirectory(_directory!);
        var file = Path.Combine(_directory!, $"pickle-{DateTime.Now:yyyyMMdd}.log");
        var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        return new StreamWriter(stream, new UTF8Encoding(false));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}

public sealed class NullLogger : IPickleLogger
{
    public static NullLogger Instance { get; } = new();

    public void Log(PickleLogLevel level, string category, string message, Exception? exception = null)
    {
    }
}
