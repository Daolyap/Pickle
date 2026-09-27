using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Tui.Panels.SystemMonitoring;

namespace Pickle.Tui.Panels.Devious;

/// <summary>A text file loaded for a pane: its display name and lines.</summary>
internal sealed record DeviousDocument(string Name, IReadOnlyList<string> Lines);

/// <summary>A slice of a real binary file for the hex pane.</summary>
internal sealed record DeviousBytes(string Name, long Offset, byte[] Bytes);

/// <summary>
/// The real data behind <c>pk devious</c>, gathered off the UI thread: processes, connections, throughput and the
/// machine summary from the system monitors, the current repository's log, a tree of the current folder, source
/// files, binaries and logs found on disk, and SHA-256 sums computed as files are found. Nothing is written or sent.
/// </summary>
internal sealed class DeviousSources(IPickleContext pickle, string cwd, int seed)
{
    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".ps1", ".psm1", ".py", ".js", ".ts", ".tsx", ".jsx", ".go", ".rs", ".java", ".kt", ".c", ".h", ".cpp",
        ".hpp", ".rb", ".php", ".swift", ".sh", ".lua", ".sql", ".vue", ".svelte", ".fs",
    };

    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "bin", "obj", ".vs", ".idea", "dist", "build", "target", "__pycache__", ".venv", "venv",
    };

    private readonly object _gate = new();
    private readonly Random _random = new(seed);
    private readonly SampleHistory _cpu = new();
    private readonly SampleHistory _down = new();
    private readonly SampleHistory _up = new();
    private readonly List<string> _codeFiles = [];
    private readonly List<string> _binaryFiles = [];
    private readonly List<string> _logFiles = [];
    private readonly List<string> _hashable = [];
    private readonly ConcurrentQueue<string> _hashes = new();
    private readonly ConcurrentQueue<DeviousDocument> _code = new();
    private readonly ConcurrentQueue<DeviousDocument> _logs = new();
    private readonly ConcurrentQueue<DeviousBytes> _bytes = new();
    private int _hashIndex;

    public ProcessSnapshot? Processes { get; private set; }

    public IReadOnlyList<NetworkConnection> Connections { get; private set; } = [];

    public IReadOnlyList<NetworkInterfaceSample> Interfaces { get; private set; } = [];

    public SystemSummary? System { get; private set; }

    public IReadOnlyList<GitCommit> Commits { get; private set; } = [];

    public string? RepositoryName { get; private set; }

    public IReadOnlyList<string> Tree { get; private set; } = [];

    public string Cwd => cwd;

    public bool HasCode => Count(_codeFiles) > 0 || !_code.IsEmpty;

    public bool HasBinaries => Count(_binaryFiles) > 0 || !_bytes.IsEmpty;

    public bool HasLogs => Count(_logFiles) > 0 || !_logs.IsEmpty;

    public IReadOnlyList<double> CpuHistory => Snapshot(_cpu);

    public IReadOnlyList<double> DownHistory => Snapshot(_down);

    public IReadOnlyList<double> UpHistory => Snapshot(_up);

    public bool TryTakeHash(out string line) => _hashes.TryDequeue(out line!);

    public DeviousDocument? NextCode() => _code.TryDequeue(out var doc) ? doc : null;

    public DeviousDocument? NextLog() => _logs.TryDequeue(out var doc) ? doc : null;

    public DeviousBytes? NextBytes() => _bytes.TryDequeue(out var bytes) ? bytes : null;

    /// <summary>Runs the loaders until <paramref name="token"/> is cancelled.</summary>
    public void Start(CancellationToken token)
    {
        Loop(TimeSpan.FromSeconds(1.5), SampleProcessesAsync, token);
        Loop(TimeSpan.FromSeconds(1), SampleNetworkAsync, token);
        Loop(TimeSpan.FromSeconds(4), SampleConnectionsAsync, token);
        Loop(TimeSpan.FromSeconds(30), SampleSystemAsync, token);
        _ = Task.Run(
            async () =>
            {
                await Guard(() => DiscoverAsync(token)).ConfigureAwait(false);
                await Guard(() => SampleGitAsync(token)).ConfigureAwait(false);
            },
            token);
        Loop(TimeSpan.FromMilliseconds(150), ct => { HashNext(ct); return Task.CompletedTask; }, token);
        Loop(TimeSpan.FromMilliseconds(500), ct => { FillDocuments(); return Task.CompletedTask; }, token);
    }

    /// <summary>One round of everything, awaited (tests).</summary>
    public async Task LoadOnceAsync(CancellationToken token)
    {
        await SampleProcessesAsync(token).ConfigureAwait(false);
        await SampleNetworkAsync(token).ConfigureAwait(false);
        await SampleConnectionsAsync(token).ConfigureAwait(false);
        await SampleSystemAsync(token).ConfigureAwait(false);
        await DiscoverAsync(token).ConfigureAwait(false);
        await SampleGitAsync(token).ConfigureAwait(false);
        for (var i = 0; i < 4; i++)
        {
            HashNext(token);
        }

        FillDocuments();
    }

    private void Loop(TimeSpan interval, Func<CancellationToken, Task> work, CancellationToken token) =>
        _ = Task.Run(
            async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    await Guard(() => work(token)).ConfigureAwait(false);
                    await Task.Delay(interval, token).ConfigureAwait(false);
                }
            },
            token);

    private async Task Guard(Func<Task> work)
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            pickle.Log.Debug("devious", "a source failed: " + ex.Message);
        }
    }

    private async Task SampleProcessesAsync(CancellationToken token)
    {
        if (pickle.Services.Get<IProcessMonitor>() is { } monitor)
        {
            var snapshot = await monitor.SampleAsync(token).ConfigureAwait(false);
            Processes = snapshot;
            Add(_cpu, snapshot.CpuPercent);
        }
    }

    private async Task SampleNetworkAsync(CancellationToken token)
    {
        if (pickle.Services.Get<INetworkMonitor>() is { } monitor)
        {
            var interfaces = await monitor.SampleInterfacesAsync(token).ConfigureAwait(false);
            Interfaces = interfaces;
            Add(_down, interfaces.Sum(i => i.ReceiveRate));
            Add(_up, interfaces.Sum(i => i.SendRate));
        }
    }

    private async Task SampleConnectionsAsync(CancellationToken token)
    {
        if (pickle.Services.Get<INetworkMonitor>() is { } monitor)
        {
            Connections = await monitor.GetConnectionsAsync(token).ConfigureAwait(false);
        }
    }

    private async Task SampleSystemAsync(CancellationToken token)
    {
        if (pickle.Services.Get<ISystemInfo>() is { } info)
        {
            System = await info.GetSummaryAsync(token).ConfigureAwait(false);
        }
    }

    private async Task SampleGitAsync(CancellationToken token)
    {
        if (pickle.Services.Get<IGitService>() is { } git && await git.FindRepositoryRootAsync(cwd, token).ConfigureAwait(false) is { } root)
        {
            Commits = await git.GetLogAsync(root, 150, graph: true, token).ConfigureAwait(false);
            RepositoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        }
    }

    /// <summary>Walks the current folder (for the tree, code and hashes) and a few system folders (binaries, logs).</summary>
    private Task DiscoverAsync(CancellationToken token) => Task.Run(
        () =>
        {
            var tree = new List<string> { cwd };
            Walk(cwd, string.Empty, 0, tree, token);
            Tree = tree;

            foreach (var root in BinaryRoots())
            {
                foreach (var file in Files(root, "*", 400))
                {
                    if (IsBinaryCandidate(file))
                    {
                        AddTo(_binaryFiles, file);
                    }
                }
            }

            foreach (var file in LogCandidates())
            {
                AddTo(_logFiles, file);
            }
        },
        token);

    private void Walk(string directory, string prefix, int depth, List<string> tree, CancellationToken token)
    {
        if (depth > 4 || tree.Count > 3000 || token.IsCancellationRequested)
        {
            return;
        }

        List<FileSystemInfo> entries;
        try
        {
            entries = [.. new DirectoryInfo(directory).EnumerateFileSystemInfos("*", Options()).OrderBy(e => e is FileInfo).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Take(60)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var last = i == entries.Count - 1;
            tree.Add(prefix + (last ? "└── " : "├── ") + entry.Name + (entry is DirectoryInfo ? "/" : string.Empty));
            if (entry is DirectoryInfo dir)
            {
                if (!SkippedFolders.Contains(dir.Name))
                {
                    Walk(dir.FullName, prefix + (last ? "    " : "│   "), depth + 1, tree, token);
                }
            }
            else if (entry is FileInfo file)
            {
                if (CodeExtensions.Contains(file.Extension) && file.Length is > 0 and < 256 * 1024)
                {
                    AddTo(_codeFiles, file.FullName);
                }

                if (file.Length is > 0 and < 16 * 1024 * 1024)
                {
                    AddTo(_hashable, file.FullName);
                }
            }
        }
    }

    private void HashNext(CancellationToken token)
    {
        if (_hashes.Count > 200)
        {
            return;
        }

        string? path;
        lock (_gate)
        {
            if (_hashable.Count == 0)
            {
                return;
            }

            path = _hashable[_hashIndex++ % _hashable.Count];
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            _hashes.Enqueue($"{hash}  {Relative(path)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void FillDocuments()
    {
        if (_code.Count < 2 && Pick(_codeFiles) is { } code && ReadLines(code, 400) is { Count: > 0 } lines)
        {
            _code.Enqueue(new DeviousDocument(Relative(code), lines));
        }

        if (_logs.Count < 2 && Pick(_logFiles) is { } log && ReadTail(log, 300) is { Count: > 0 } tail)
        {
            _logs.Enqueue(new DeviousDocument(log, tail));
        }

        if (_bytes.Count < 2 && Pick(_binaryFiles) is { } binary)
        {
            try
            {
                using var stream = new FileStream(binary, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var offset = stream.Length > 8192 ? (long)(_random.NextDouble() * (stream.Length - 4096)) & ~0xFL : 0;
                stream.Position = offset;
                var buffer = new byte[(int)Math.Min(4096, stream.Length - offset)];
                stream.ReadExactly(buffer);
                _bytes.Enqueue(new DeviousBytes(Path.GetFileName(binary), offset, buffer));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
            {
            }
        }
    }

    private string? Pick(List<string> list)
    {
        lock (_gate)
        {
            return list.Count == 0 ? null : list[_random.Next(list.Count)];
        }
    }

    private string Relative(string path)
    {
        var relative = Path.GetRelativePath(cwd, path);
        return relative.StartsWith("..", StringComparison.Ordinal) ? path : relative;
    }

    private static IEnumerable<string> BinaryRoots()
    {
        yield return AppContext.BaseDirectory;
        if (OperatingSystem.IsWindows())
        {
            yield return Environment.SystemDirectory;
        }
        else
        {
            yield return "/usr/bin";
            yield return "/usr/lib";
        }
    }

    private static bool IsBinaryCandidate(string file)
    {
        var extension = Path.GetExtension(file);
        return OperatingSystem.IsWindows()
            ? extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) || extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            : extension.Length == 0 || extension.Equals(".so", StringComparison.Ordinal) || extension.Equals(".dll", StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerable<string> LogCandidates()
    {
        foreach (var file in Files(pickle.Paths.LogDir, "*.log", 20))
        {
            yield return file;
        }

        string[] system = OperatingSystem.IsWindows()
            ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Logs", "DISM", "dism.log"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "WindowsUpdate.log")]
            : ["/var/log/syslog", "/var/log/messages", "/var/log/dpkg.log", "/var/log/dnf.log", "/var/log/pacman.log", "/var/log/alternatives.log"];
        foreach (var file in system)
        {
            if (File.Exists(file))
            {
                yield return file;
            }
        }
    }

    private static IEnumerable<string> Files(string directory, string pattern, int max)
    {
        try
        {
            return Directory.Exists(directory) ? [.. Directory.EnumerateFiles(directory, pattern, Options()).Take(max)] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static EnumerationOptions Options() => new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Hidden,
        RecurseSubdirectories = false,
    };

    private static List<string>? ReadLines(string path, int max)
    {
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var lines = new List<string>();
            while (lines.Count < max && reader.ReadLine() is { } line)
            {
                lines.Add(line.Replace("\t", "    ", StringComparison.Ordinal));
            }

            return lines.Any(l => l.Contains('\0', StringComparison.Ordinal)) ? null : lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static List<string>? ReadTail(string path, int max)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Position = Math.Max(0, stream.Length - (64 * 1024));
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
            return lines.Count > max ? lines[^max..] : lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void AddTo(List<string> list, string item)
    {
        lock (_gate)
        {
            list.Add(item);
        }
    }

    private int Count(List<string> list)
    {
        lock (_gate)
        {
            return list.Count;
        }
    }

    private void Add(SampleHistory history, double value)
    {
        lock (_gate)
        {
            history.Add(value);
        }
    }

    private IReadOnlyList<double> Snapshot(SampleHistory history)
    {
        lock (_gate)
        {
            return [.. history.Values];
        }
    }

    /// <summary>"12.3%" etc. for the panes.</summary>
    public static string Percent(double value) => value.ToString("0.0", CultureInfo.InvariantCulture) + "%";
}
