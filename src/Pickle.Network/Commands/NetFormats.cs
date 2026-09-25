using System.Management.Automation;
using Pickle.Abstractions;

namespace Pickle.Network.Commands;

/// <summary>
/// Table views for rows with more than four columns (PowerShell would list them otherwise). The format file is written
/// to the data folder and loaded into the main runspace the first time a network command runs.
/// </summary>
internal static class NetFormats
{
    private static readonly object Gate = new();
    private static IPickleShell? _loadedFor;

    /// <summary><see cref="Display.Columns"/> plus a type name the table views select on.</summary>
    public static PSObject Row(string view, object item, params DisplayColumn[] columns)
    {
        var row = Display.Columns(item, columns);
        row.TypeNames.Insert(0, "Pickle.Network." + view);
        return row;
    }

    public static async Task EnsureLoadedAsync(IPickleContext pickle)
    {
        lock (Gate)
        {
            if (ReferenceEquals(_loadedFor, pickle.Shell))
            {
                return;
            }

            _loadedFor = pickle.Shell;
        }

        try
        {
            var path = Path.Combine(pickle.Paths.DataDir, "formats", "Pickle.Network.format.ps1xml");
            using (var stream = typeof(NetFormats).Assembly.GetManifestResourceStream("Pickle.Network.format.ps1xml")!)
            using (var reader = new StreamReader(stream))
            {
                var xml = await reader.ReadToEndAsync().ConfigureAwait(false);
                if (!File.Exists(path) || await File.ReadAllTextAsync(path).ConfigureAwait(false) != xml)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllTextAsync(path, xml).ConfigureAwait(false);
                }
            }

            await pickle.Shell.InvokeAsync("param($p) Update-FormatData -PrependPath $p", new Dictionary<string, object?> { ["p"] = path }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or RuntimeException)
        {
            // Without the views the rows still print, as lists.
            pickle.Log.Warn("network", "Could not load the network table views", ex);
        }
    }
}
