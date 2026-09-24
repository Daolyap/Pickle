using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using Pickle.Abstractions;

namespace Pickle.Core.Plugins;

public enum PluginStatus
{
    Loaded,
    Disabled,
    Untrusted,
    Failed,
}

/// <summary>What <c>Get-PicklePlugin</c> / <c>pk plugin list</c> show about one plugin.</summary>
public sealed class PluginInfo
{
    public const string BuiltInKind = "built-in";
    public const string PowerShellKind = "powershell";
    public const string DotnetKind = "dotnet";

    public required string Id { get; init; }

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>"built-in", "powershell" or "dotnet".</summary>
    public required string Kind { get; init; }

    public PluginStatus Status { get; set; }

    public string? Version { get; set; }

    public string? Description { get; set; }

    /// <summary>Module manifest or main assembly.</summary>
    public string? Path { get; set; }

    /// <summary>SHA-256 of the main assembly (.NET plugins).</summary>
    public string? Hash { get; set; }

    public string? Error { get; set; }

    /// <summary>What the plugin registered, e.g. "command: hello", "segment: weather".</summary>
    public List<string> Contributions { get; } = [];

    public override string ToString() => $"{Id} ({Kind}, {Status})";
}

public sealed record PowerShellPluginCandidate(string Id, string ImportTarget, string? Path, string Origin);

public sealed record DotnetPluginCandidate(string Id, string AssemblyPath, string? TypeName, string Directory);

/// <summary>Plugins installed with <c>pk plugin install</c> (ConfigDir/plugins.json, synced as a list of names).</summary>
public sealed record InstalledPlugin(string Name, string Source, string? Location);

public static class InstalledPlugins
{
    public const string FileName = "plugins.json";

    public static string PathFor(PicklePaths paths) => System.IO.Path.Combine(paths.ConfigDir, FileName);

    public static List<InstalledPlugin> Read(PicklePaths paths)
    {
        var file = PathFor(paths);
        if (!File.Exists(file))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(file), PickleJson.Options)?.Plugins ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static void Write(PicklePaths paths, IEnumerable<InstalledPlugin> plugins)
    {
        Directory.CreateDirectory(paths.ConfigDir);
        var manifest = new Manifest { Plugins = [.. plugins.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)] };
        File.WriteAllText(PathFor(paths), JsonSerializer.Serialize(manifest, PickleJson.Options) + Environment.NewLine);
    }

    public static void Add(PicklePaths paths, InstalledPlugin plugin)
    {
        var list = Read(paths);
        list.RemoveAll(p => string.Equals(p.Name, plugin.Name, StringComparison.OrdinalIgnoreCase));
        list.Add(plugin);
        Write(paths, list);
    }

    public static bool Remove(PicklePaths paths, string name)
    {
        var list = Read(paths);
        var removed = list.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
        {
            Write(paths, list);
        }

        return removed;
    }

    private sealed class Manifest
    {
        public List<InstalledPlugin> Plugins { get; set; } = [];
    }
}

/// <summary>
/// Isolates a .NET plugin and its dependencies. Pickle's own assemblies and PowerShell are shared with the default
/// context so plugin types implement the host's <see cref="IPicklePlugin"/> and exchange PSObjects freely.
/// </summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver? _resolver;
    private readonly string _directory;

    public PluginLoadContext(string mainAssemblyPath)
        : base("pickle-plugin:" + System.IO.Path.GetFileNameWithoutExtension(mainAssemblyPath), isCollectible: false)
    {
        _directory = System.IO.Path.GetDirectoryName(mainAssemblyPath)!;
        try
        {
            _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
        }
        catch (InvalidOperationException)
        {
            // Not available in single-file hosts; fall back to probing the plugin folder.
        }
    }

    public static bool IsShared(string name) =>
        name is "Pickle.Abstractions" or "System.Management.Automation"
        || name.StartsWith("Microsoft.PowerShell.", StringComparison.OrdinalIgnoreCase)
        || (name.StartsWith("Pickle.", StringComparison.OrdinalIgnoreCase) && Default.Assemblies.Any(a => a.GetName().Name == name));

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is null || IsShared(assemblyName.Name))
        {
            return null;
        }

        var path = _resolver?.ResolveAssemblyToPath(assemblyName);
        if (path is null)
        {
            var probe = System.IO.Path.Combine(_directory, assemblyName.Name + ".dll");
            path = File.Exists(probe) ? probe : null;
        }

        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}

public static class AssemblyInspector
{
    public static string Sha256(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static string NormalizeHash(string hash)
    {
        var trimmed = hash.Trim();
        if (trimmed.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[7..];
        }

        return trimmed.ToUpperInvariant();
    }

    /// <summary>True when the file is a .NET assembly that references Pickle.Abstractions. Reads metadata only; runs no code.</summary>
    public static bool ReferencesPickleAbstractions(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
            {
                return false;
            }

            var reader = pe.GetMetadataReader();
            foreach (var handle in reader.AssemblyReferences)
            {
                if (reader.GetString(reader.GetAssemblyReference(handle).Name) == "Pickle.Abstractions")
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
        }

        return false;
    }
}
