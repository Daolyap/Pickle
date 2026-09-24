using System.Diagnostics;
using System.Globalization;
using System.IO.Enumeration;
using System.Security;
using Pickle.Abstractions.Services;

namespace Pickle.Core.SystemMonitoring;

/// <summary>
/// A du-like scan: builds a tree of every directory (sizes include the whole subtree) and the largest files of each.
/// Symlinks, junctions and other reparse points are never followed, other file systems mounted inside the tree are
/// skipped (like <c>du -x</c>), and directories that can't be read are counted in <see cref="DiskUsageNode.Skipped"/>.
/// </summary>
internal sealed class DiskUsageScanner
{
    public const int DefaultMaxFiles = 100;

    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    private readonly HashSet<string> _mountPoints;
    private readonly int _maxFiles;
    private readonly TimeSpan _progressInterval;

    public DiskUsageScanner(IEnumerable<string>? mountPoints = null, int maxFiles = DefaultMaxFiles, TimeSpan? progressInterval = null)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        _mountPoints = new HashSet<string>((mountPoints ?? []).Select(Normalize), comparer);
        _maxFiles = Math.Max(1, maxFiles);
        _progressInterval = progressInterval ?? TimeSpan.FromMilliseconds(100);
    }

    public DiskUsageNode Scan(string path, IProgress<DiskScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var rootPath = Normalize(Path.GetFullPath(path));
        if (!Directory.Exists(rootPath))
        {
            throw new DirectoryNotFoundException($"Folder not found: {path}");
        }

        var root = new DiskUsageNode(rootPath, isDirectory: true, path: rootPath);
        var directories = new List<DiskUsageNode> { root };
        var pending = new Stack<DiskUsageNode>();
        pending.Push(root);
        long files = 0, bytes = 0;
        var clock = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        var entries = new List<(string Name, long Length)>();

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            var directoryPath = directory.FullPath;
            entries.Clear();
            try
            {
                var enumerable = new FileSystemEnumerable<Entry>(directoryPath, static (ref FileSystemEntry e) => new Entry(e.FileName.ToString(), e.IsDirectory, e.IsDirectory ? 0 : e.Length, e.Attributes), Options);
                foreach (var entry in enumerable)
                {
                    var isLink = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
                    if (entry.IsDirectory)
                    {
                        var childPath = Path.Join(directoryPath, entry.Name);
                        if (isLink || _mountPoints.Contains(childPath))
                        {
                            directory.Skipped++;
                            continue;
                        }

                        var child = new DiskUsageNode(entry.Name, isDirectory: true, directory) { DirectoryCount = 1 };
                        directory.AddChild(child);
                        directories.Add(child);
                        pending.Push(child);
                    }
                    else if (isLink && IsSymbolicLink(Path.Join(directoryPath, entry.Name)))
                    {
                        directory.Skipped++;
                    }
                    else
                    {
                        entries.Add((entry.Name, entry.Length));
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
            {
                directory.Skipped++;
            }

            AddFiles(directory, entries);
            files += entries.Count;
            bytes += directory.Size;
            if (progress is not null && clock.Elapsed - lastReport >= _progressInterval)
            {
                lastReport = clock.Elapsed;
                progress.Report(new DiskScanProgress(directories.Count, files, bytes, directoryPath));
            }
        }

        // Children were created after their parents, so walking backwards folds every subtree into its parent.
        for (var i = directories.Count - 1; i > 0; i--)
        {
            var node = directories[i];
            var parent = node.Parent!;
            parent.Size += node.Size;
            parent.FileCount += node.FileCount;
            parent.DirectoryCount += node.DirectoryCount;
            parent.Skipped += node.Skipped;
        }

        foreach (var node in directories)
        {
            node.SortChildren();
        }

        progress?.Report(new DiskScanProgress(directories.Count, files, bytes, rootPath));
        return root;
    }

    private void AddFiles(DiskUsageNode directory, List<(string Name, long Length)> entries)
    {
        if (entries.Count > _maxFiles)
        {
            entries.Sort(static (a, b) => b.Length.CompareTo(a.Length));
        }

        for (var i = 0; i < entries.Count && i < _maxFiles; i++)
        {
            directory.AddChild(new DiskUsageNode(entries[i].Name, isDirectory: false, directory) { Size = entries[i].Length, FileCount = 1 });
        }

        if (entries.Count > _maxFiles)
        {
            var rest = entries.Count - _maxFiles;
            var restSize = entries.Skip(_maxFiles).Sum(e => e.Length);
            directory.AddChild(new DiskUsageNode($"({rest.ToString("N0", CultureInfo.InvariantCulture)} smaller files)", isDirectory: false, directory)
            {
                IsGroup = true,
                Size = restSize,
                FileCount = rest,
            });
        }

        directory.Size = entries.Sum(e => e.Length);
        directory.FileCount = entries.Count;
    }

    // On Windows a file can be a reparse point without being a link (deduplicated, compressed or cloud files):
    // those are counted; only real symbolic links are skipped.
    private static bool IsSymbolicLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string Normalize(string path)
    {
        var root = Path.GetPathRoot(path);
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return root is not null && trimmed.Length < root.Length ? root : trimmed.Length == 0 ? path : trimmed;
    }

    private readonly record struct Entry(string Name, bool IsDirectory, long Length, FileAttributes Attributes);
}
