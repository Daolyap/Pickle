using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pickle.Abstractions;

namespace Pickle.Windows.Terminal;

/// <summary>
/// Command-line entry points (<c>pickle --install-terminal-profile</c> / <c>--uninstall-terminal-profile</c>), which
/// run without a shell runtime: settings and palette are read from config.json and the configured theme on disk.
/// Inside the shell, <c>pk terminal</c> (<see cref="WindowsTerminalIntegration"/>) does the same with live state.
/// </summary>
public static class WindowsTerminalProfile
{
    public const string WindowsOnlyMessage = "Windows Terminal integration is only available on Windows.";

    public static int Install(string executablePath) =>
        Install(executablePath, Console.Out, Console.Error, WindowsTerminalLocations.ForCurrentUser(), PicklePaths.Resolve());

    public static int Install(string executablePath, TextWriter output, TextWriter error, WindowsTerminalLocations? locations, PicklePaths paths)
    {
        if (locations is null)
        {
            error.WriteLine("pickle: " + WindowsOnlyMessage);
            return 1;
        }

        var (settings, palette, themeName) = LoadFromDisk(paths);
        try
        {
            new WindowsTerminalManager(locations).Install(executablePath, settings, palette);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"pickle: could not write {locations.FragmentFile}: {ex.Message}");
            return 1;
        }

        output.WriteLine($"Installed the Windows Terminal profile '{WindowsTerminalFragment.ProfileName}' ({themeName} colors): {locations.FragmentFile}");
        output.WriteLine("Restart Windows Terminal to pick it up. To make it the default profile, run: pk terminal default");
        return 0;
    }

    public static int Uninstall() => Uninstall(Console.Out, Console.Error, WindowsTerminalLocations.ForCurrentUser());

    public static int Uninstall(TextWriter output, TextWriter error, WindowsTerminalLocations? locations)
    {
        if (locations is null)
        {
            error.WriteLine("pickle: " + WindowsOnlyMessage);
            return 1;
        }

        try
        {
            output.WriteLine(new WindowsTerminalManager(locations).Uninstall()
                ? $"Removed the Windows Terminal profile ({locations.FragmentFile})."
                : "The Windows Terminal profile is not installed.");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"pickle: could not remove {locations.FragmentFile}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Fragment JSON for an installer (e.g. the MSI's all-users fragment under %ProgramData%): the commandline is used
    /// verbatim, appearance comes from default <see cref="TerminalSettings"/> and the built-in "pickle" palette unless
    /// given. No files are touched.
    /// </summary>
    public static string BuildFragment(string commandline, TerminalSettings? settings = null, TerminalPalette? palette = null, string? icon = null) =>
        WindowsTerminalFragment.BuildForCommandline(
            commandline,
            settings ?? new TerminalSettings(),
            palette ?? LoadEmbeddedTheme("pickle")?.Terminal ?? new TerminalPalette(),
            icon);

    /// <summary>Writes <see cref="BuildFragment"/> to <paramref name="path"/> (UTF-8, no BOM), creating its directory. Returns an exit code.</summary>
    public static int WriteFragment(string path, string commandline, TextWriter? error = null, string? icon = null)
    {
        if (string.IsNullOrWhiteSpace(commandline))
        {
            error?.WriteLine("pickle: --fragment-commandline must not be empty.");
            return 2;
        }

        try
        {
            var full = Path.GetFullPath(path);
            if (Path.GetDirectoryName(full) is { Length: > 0 } dir)
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(full, BuildFragment(commandline, icon: icon), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error?.WriteLine($"pickle: could not write {path}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>Regenerates the installed fragment for the current user; false when not installed or unchanged.</summary>
    public static bool Update(TerminalSettings settings, TerminalPalette palette, WindowsTerminalLocations? locations = null) =>
        (locations ?? WindowsTerminalLocations.ForCurrentUser()) is { } l && new WindowsTerminalManager(l).Update(settings, palette);

    /// <summary>Terminal settings (config.json overlaid with config.local.json) and the configured theme's palette.</summary>
    public static (TerminalSettings Settings, TerminalPalette Palette, string ThemeName) LoadFromDisk(PicklePaths paths)
    {
        var config = new PickleConfig();
        try
        {
            var node = ReadObject(paths.ConfigFile) ?? new JsonObject();
            if (ReadObject(paths.LocalConfigFile) is { } local)
            {
                Merge(node, local);
            }

            config = node.Deserialize<PickleConfig>(PickleJson.Options) ?? config;
        }
        catch (JsonException)
        {
        }

        var palette = LoadTheme(paths, config.Theme)?.Terminal ?? LoadTheme(paths, "pickle")?.Terminal ?? new TerminalPalette();
        return (config.Terminal, palette, config.Theme);
    }

    private static Theme? LoadTheme(PicklePaths paths, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains("..", StringComparison.Ordinal)
            || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
        {
            return null;
        }

        try
        {
            var userFile = Path.Combine(paths.ThemesDir, name + ".json");
            if (File.Exists(userFile))
            {
                return JsonSerializer.Deserialize<Theme>(File.ReadAllText(userFile), PickleJson.Options);
            }

            return LoadEmbeddedTheme(name);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Built-in themes are embedded in Pickle.Core, which this assembly does not reference; it is loaded in the pickle
    // process, so find it at runtime.
    private static Theme? LoadEmbeddedTheme(string name)
    {
        try
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic)
                {
                    continue;
                }

                using var stream = assembly.GetManifestResourceStream("Pickle.Themes." + name + ".json");
                if (stream is not null)
                {
                    return JsonSerializer.Deserialize<Theme>(stream, PickleJson.Options);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
        }

        return null;
    }

    private static JsonObject? ReadObject(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(
                File.ReadAllText(path),
                documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source.ToList())
        {
            if (value is JsonObject sourceChild && target[key] is JsonObject targetChild)
            {
                Merge(targetChild, sourceChild);
            }
            else
            {
                target[key] = value?.DeepClone();
            }
        }
    }
}
