using System.Security.Cryptography;
using System.Text;
using Pickle.Abstractions;

namespace Pickle.Core.Hosting;

/// <summary>
/// Extracts the PowerShell modules embedded in Pickle.Core (src/Pickle.Core/Modules/**) to
/// DataDir/modules/&lt;hash&gt;/ once per build, so they load like normal modules and show up in Get-Module.
/// </summary>
public static class EmbeddedModules
{
    private const string Prefix = "Pickle.Modules.";

    public static string Extract(PicklePaths paths, IPickleLogger log)
    {
        var assembly = typeof(EmbeddedModules).Assembly;
        var resources = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var contents = new List<(string RelativePath, byte[] Bytes)>();
        foreach (var name in resources)
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var bytes = ms.ToArray();
            var relative = name[Prefix.Length..].Replace('\\', '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData(bytes);
            contents.Add((relative, bytes));
        }

        var id = Convert.ToHexString(hash.GetHashAndReset())[..16].ToLowerInvariant();
        var root = Path.Combine(paths.DataDir, "modules", id);
        var marker = Path.Combine(root, ".complete");
        if (File.Exists(marker))
        {
            return root;
        }

        try
        {
            foreach (var (relative, bytes) in contents)
            {
                var target = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, bytes);
            }

            File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O"));
            CleanupOld(Path.Combine(paths.DataDir, "modules"), id);
        }
        catch (IOException ex)
        {
            log.Error("modules", $"Failed to extract embedded modules to {root}", ex);
        }

        return root;
    }

    private static void CleanupOld(string modulesRoot, string keep)
    {
        foreach (var dir in Directory.EnumerateDirectories(modulesRoot))
        {
            if (Path.GetFileName(dir) == keep)
            {
                continue;
            }

            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Another Pickle instance may still be using it.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
