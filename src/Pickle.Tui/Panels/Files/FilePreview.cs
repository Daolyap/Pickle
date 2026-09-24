using System.Globalization;
using System.Text;
using Pickle.Tui.Widgets;

namespace Pickle.Tui.Panels.Files;

/// <summary>Preview content for the file picker: text head with line numbers, directory listing, or binary metadata.</summary>
public sealed record FilePreviewContent(string Title, IReadOnlyList<PreviewLine> Lines, bool LineNumbers);

public static class FilePreview
{
    private const int MaxBytes = 64 * 1024;
    private const int BinaryProbe = 8 * 1024;

    public static FilePreviewContent Build(string path, int maxLines = 300)
    {
        try
        {
            if (Directory.Exists(path))
            {
                return Listing(path, maxLines);
            }

            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return new FilePreviewContent(Path.GetFileName(path), [new PreviewLine("(not found)", Muted: true)], false);
            }

            var buffer = new byte[(int)Math.Min(MaxBytes, info.Length)];
            int read;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            }

            var utf16 = read >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE;
            if (!utf16 && Array.IndexOf(buffer, (byte)0, 0, Math.Min(read, BinaryProbe)) >= 0)
            {
                return Metadata(info, "binary");
            }

            var text = Decode(buffer.AsSpan(0, read));
            var lines = text.Split('\n').Take(maxLines).Select(l => new PreviewLine(l.TrimEnd('\r'))).ToList();
            if (lines.Count > 0 && lines[^1].Text.Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }

            var title = $"{info.Name}  ·  {FormatSize(info.Length)}";
            return new FilePreviewContent(title, lines.Count == 0 ? [new PreviewLine("(empty file)", Muted: true)] : lines, lines.Count > 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new FilePreviewContent(Path.GetFileName(path), [new PreviewLine(ex.Message, Muted: true)], false);
        }
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} B"
            : value.ToString(value < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    private static FilePreviewContent Listing(string path, int maxLines)
    {
        var dir = new DirectoryInfo(path);
        var entries = dir.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true })
            .OrderBy(e => e is DirectoryInfo ? 0 : 1)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Take(maxLines + 1)
            .ToList();
        var lines = entries.Take(maxLines)
            .Select(e => e is DirectoryInfo
                ? new PreviewLine(e.Name + Path.DirectorySeparatorChar)
                : new PreviewLine($"{e.Name}  {FormatSize(((FileInfo)e).Length)}"))
            .ToList();
        if (entries.Count > maxLines)
        {
            lines.Add(new PreviewLine("…", Muted: true));
        }

        if (lines.Count == 0)
        {
            lines.Add(new PreviewLine("(empty directory)", Muted: true));
        }

        return new FilePreviewContent(dir.Name + Path.DirectorySeparatorChar, lines, false);
    }

    private static FilePreviewContent Metadata(FileInfo info, string kind)
    {
        var lines = new List<PreviewLine>
        {
            new($"Type      {kind}{(info.Extension.Length > 0 ? " (" + info.Extension + ")" : string.Empty)}"),
            new($"Size      {FormatSize(info.Length)} ({info.Length.ToString("N0", CultureInfo.InvariantCulture)} bytes)"),
            new($"Modified  {info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}"),
            new($"Created   {info.CreationTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}"),
            new($"Attributes {info.Attributes}"),
        };
        return new FilePreviewContent(info.Name, lines, false);
    }

    private static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith("﻿"u8))
        {
            return Encoding.UTF8.GetString(bytes[3..]);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes[2..]);
        }

        return Encoding.UTF8.GetString(bytes);
    }
}
