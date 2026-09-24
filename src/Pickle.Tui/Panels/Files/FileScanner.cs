namespace Pickle.Tui.Panels.Files;

/// <summary>A file or directory found under the picker root.</summary>
public sealed record FileEntry(string FullPath, string RelativePath, bool IsDirectory, long Size, DateTime Modified)
{
    public string Display => IsDirectory ? RelativePath + Path.DirectorySeparatorChar : RelativePath;
}

public sealed record FileScanOptions
{
    public static IReadOnlySet<string> DefaultIgnored { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".git", "node_modules", "bin", "obj" };

    public bool IncludeHidden { get; init; }

    /// <summary>Skip <see cref="DefaultIgnored"/> directories (their contents are not scanned).</summary>
    public bool SkipIgnored { get; init; } = true;

    public bool DirectoriesOnly { get; init; }

    public int MaxEntries { get; init; } = 300_000;

    public int MaxDepth { get; init; } = 32;
}

/// <summary>Breadth-first directory walk (shallow entries first, names sorted per directory), in batches.</summary>
public static class FileScanner
{
    public static void Scan(string root, FileScanOptions options, Action<IReadOnlyList<FileEntry>> onBatch, CancellationToken cancellationToken, int batchSize = 512)
    {
        var enumeration = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = options.IncludeHidden ? 0 : FileAttributes.Hidden | FileAttributes.System,
        };
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        var batch = new List<FileEntry>(batchSize);
        var count = 0;
        while (queue.Count > 0 && count < options.MaxEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (dir, depth) = queue.Dequeue();
            FileSystemInfo[] children;
            try
            {
                children = new DirectoryInfo(dir).GetFileSystemInfos("*", enumeration);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }

            Array.Sort(children, (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
            foreach (var child in children)
            {
                var isDir = child is DirectoryInfo;
                if (!options.IncludeHidden && child.Name.StartsWith('.'))
                {
                    continue;
                }

                if (isDir && options.SkipIgnored && FileScanOptions.DefaultIgnored.Contains(child.Name))
                {
                    continue;
                }

                if (isDir && depth + 1 < options.MaxDepth && !child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    queue.Enqueue((child.FullName, depth + 1));
                }

                if (options.DirectoriesOnly && !isDir)
                {
                    continue;
                }

                batch.Add(new FileEntry(
                    child.FullName,
                    Path.GetRelativePath(root, child.FullName),
                    isDir,
                    child is FileInfo file ? SafeLength(file) : 0,
                    child.LastWriteTime));
                count++;
                if (batch.Count >= batchSize)
                {
                    onBatch(batch);
                    batch = new List<FileEntry>(batchSize);
                }

                if (count >= options.MaxEntries)
                {
                    break;
                }
            }
        }

        if (batch.Count > 0)
        {
            onBatch(batch);
        }
    }

    private static long SafeLength(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
