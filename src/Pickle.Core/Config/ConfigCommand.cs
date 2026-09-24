using System.Collections;
using System.Management.Automation;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pickle.Abstractions;
using Pickle.Core.Commands;

namespace Pickle.Core.Config;

/// <summary>Reads settings for display and scripting (<c>pk config get</c>, <c>Get-PickleConfig</c>).</summary>
public static class ConfigValues
{
    /// <summary>The effective value at a path: typed settings objects for sections, primitives/lists for leaves.</summary>
    public static object? Get(PickleConfig config, ConfigPathInfo info)
    {
        object? current = config;
        for (var i = 0; i < info.Segments.Count; i++)
        {
            var segment = info.Segments[i];
            switch (current)
            {
                case null:
                    return null;
                case JsonElement element:
                    var node = JsonConfigStore.Find(new JsonObject { ["v"] = JsonNode.Parse(element.GetRawText()) }, ["v", .. info.Segments.Skip(i)]);
                    return PsJson.ToPowerShell(node);
                case IDictionary dictionary:
                    current = null;
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        if (string.Equals(entry.Key as string, segment, StringComparison.OrdinalIgnoreCase))
                        {
                            current = entry.Value;
                            break;
                        }
                    }

                    break;
                default:
                    var property = ConfigSchema.SettableProperties(current.GetType())
                        .FirstOrDefault(p => string.Equals(ConfigSchema.JsonName(p), segment, StringComparison.OrdinalIgnoreCase));
                    current = property?.GetValue(current);
                    break;
            }
        }

        return current is JsonElement last ? PsJson.ToPowerShell(last) : current;
    }

    /// <summary>Compact JSON for display, e.g. <c>"visual"</c>, <c>true</c>, <c>["ls"]</c>.</summary>
    public static string Display(JsonElement? value) => value is { } v ? v.GetRawText() : "null";

    /// <summary>Leaf values of a config layer as (dotted path, JSON text).</summary>
    public static IEnumerable<(string Path, string Json)> Flatten(JsonObject layer, string prefix = "")
    {
        foreach (var (key, value) in layer)
        {
            if (prefix.Length == 0 && key == "$schema")
            {
                continue;
            }

            var path = prefix + key;
            if (value is JsonObject obj && obj.Count > 0)
            {
                foreach (var item in Flatten(obj, path + "."))
                {
                    yield return item;
                }
            }
            else
            {
                yield return (path, value?.ToJsonString() ?? "null");
            }
        }
    }
}

/// <summary><c>pk config</c>: view and change settings.</summary>
public sealed class ConfigCommand(PickleRuntime runtime) : IPickleCommand
{
    public string Name => "config";

    public string Description => "View and change settings (config.json / config.local.json)";

    public string Usage => """
        pk config                                Summary and the settings you changed
        pk config get [<path>]                   Show a value, e.g. pk config get editor.bellStyle
        pk config set <path> <value> [--local]   Change a value (--local: this machine only, never synced)
        pk config reset <path> [--local]         Remove your value so the default applies
        pk config list                           Every setting with its value and where it comes from
        pk config edit [--local|--profile]       Open the file in your editor
        pk config path [--local]                 Print the file path
        pk config schema                         Print the JSON schema
        """;

    private JsonConfigStore Store => runtime.ConfigStore;

    public ValueTask<int> ExecuteAsync(PickleCommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var local = args.Any(a => a is "--local" or "-l");
        var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal) && a != "-l").ToList();
        var sub = positional.Count > 0 ? positional[0].ToLowerInvariant() : string.Empty;
        var rest = positional.Skip(1).ToList();
        try
        {
            return ValueTask.FromResult(sub switch
            {
                "" => Summary(context),
                "get" => Get(context, rest),
                "set" => Set(context, rest, local),
                "reset" or "unset" => Reset(context, rest, local),
                "list" or "ls" => List(context),
                "edit" => Edit(context, local, args.Contains("--profile")),
                "path" => WritePath(context, local),
                "schema" => Schema(context),
                _ => Unknown(context, sub),
            });
        }
        catch (ConfigValidationException ex)
        {
            context.WriteError(ex.Message);
            return ValueTask.FromResult(1);
        }
    }

    private int Summary(PickleCommandContext context)
    {
        var theme = runtime.Themes.Current;
        var config = Store.Current;
        string Label(string text) => Ansi.Colorize(text.PadRight(9), theme.Ui.Muted);

        context.WriteHost(Label("Config") + Store.ConfigFile);
        var localLayer = Store.GetLocalLayer();
        var localCount = ConfigValues.Flatten(localLayer).Count();
        context.WriteHost(Label("Local") + Store.LocalConfigFile + Ansi.Colorize(
            File.Exists(Store.LocalConfigFile) ? $"  ({localCount} machine-only value{(localCount == 1 ? string.Empty : "s")})" : "  (not created)",
            theme.Ui.Muted));
        context.WriteHost(Label("Theme") + config.Theme
            + Ansi.Colorize("  ·  sync: ", theme.Ui.Muted) + (config.Sync.Backend == "none" ? "off" : $"{config.Sync.Backend} → {config.Sync.Target}")
            + Ansi.Colorize("  ·  plugins: ", theme.Ui.Muted) + (config.Plugins.AutoLoad ? "auto-load" : "off"));

        var changed = ConfigValues.Flatten(Store.GetBaseLayer()).Select(v => (v.Path, v.Json, Local: false))
            .Concat(ConfigValues.Flatten(localLayer).Select(v => (v.Path, v.Json, Local: true)))
            .ToList();
        if (changed.Count > 0)
        {
            context.WriteHost(string.Empty);
            context.WriteHost(Ansi.Colorize("Your settings", theme.Ui.Accent, bold: true));
            foreach (var (path, json, isLocal) in changed)
            {
                context.WriteHost("  " + path + " = " + json + (isLocal ? Ansi.Colorize("  (local)", theme.Ui.Muted) : string.Empty));
            }
        }

        WriteProblems(context);
        context.WriteHost(string.Empty);
        context.WriteHost(Ansi.Colorize("pk config get|set|reset|list|edit|path|schema  ·  pk config --help", theme.Ui.Muted));
        return 0;
    }

    private void WriteProblems(PickleCommandContext context)
    {
        var problems = Store.Problems;
        if (problems.Count == 0)
        {
            return;
        }

        var theme = runtime.Themes.Current;
        context.WriteHost(string.Empty);
        context.WriteHost(Ansi.Colorize("Problems", theme.Ui.Warning, bold: true));
        foreach (var problem in problems)
        {
            var icon = problem.Severity == ConfigProblemSeverity.Error ? Ansi.Colorize("✖", theme.Ui.Error) : Ansi.Colorize("⚠", theme.Ui.Warning);
            context.WriteHost($"  {icon} {problem}");
        }
    }

    private int Get(PickleCommandContext context, List<string> rest)
    {
        if (rest.Count == 0)
        {
            context.WriteObject(Store.Current);
            return 0;
        }

        var info = ConfigSchema.Resolve(rest[0]);
        context.WriteObject(ConfigValues.Get(Store.Current, info));
        return 0;
    }

    private int Set(PickleCommandContext context, List<string> rest, bool local)
    {
        if (rest.Count < 2)
        {
            context.WriteError("Usage: pk config set <path> <value> [--local]   e.g. pk config set editor.bellStyle visual");
            return 2;
        }

        var path = ConfigSchema.Resolve(rest[0]).Path;
        var value = string.Join(' ', rest.Skip(1));
        if (local)
        {
            Store.SetLocalValue(path, value);
        }
        else
        {
            Store.SetValue(path, value);
        }

        var theme = runtime.Themes.Current;
        var shown = ConfigValues.Display(Store.GetValue(path));
        context.WriteHost(Ansi.Colorize("✔ ", theme.Ui.Success) + path + " = " + shown + Ansi.Colorize($"  ({(local ? JsonConfigStore.LocalSource : JsonConfigStore.BaseSource)})", theme.Ui.Muted));
        if (!local && Store.IsOverriddenLocally(path))
        {
            context.WriteHost(Ansi.Colorize("⚠ ", theme.Ui.Warning) + $"config.local.json overrides this on this machine. Use --local, or: pk config reset {path} --local");
        }

        return 0;
    }

    private int Reset(PickleCommandContext context, List<string> rest, bool local)
    {
        if (rest.Count < 1)
        {
            context.WriteError("Usage: pk config reset <path> [--local]");
            return 2;
        }

        var path = ConfigSchema.Resolve(rest[0]).Path;
        var theme = runtime.Themes.Current;
        if (!Store.Reset(path, local))
        {
            context.WriteHost(Ansi.Colorize($"{path} is not set in {(local ? JsonConfigStore.LocalSource : JsonConfigStore.BaseSource)}; nothing to reset.", theme.Ui.Muted));
            return 0;
        }

        context.WriteHost(Ansi.Colorize("✔ ", theme.Ui.Success) + $"{path} reset; now {ConfigValues.Display(Store.GetValue(path))} ({Store.GetSource(path)})");
        return 0;
    }

    private int List(PickleCommandContext context)
    {
        foreach (var path in ConfigSchema.AllPaths())
        {
            var info = ConfigSchema.Resolve(path);
            if (info.Kind == ConfigNodeKind.Section)
            {
                continue;
            }

            var item = new PSObject();
            item.Properties.Add(new PSNoteProperty("Path", path));
            item.Properties.Add(new PSNoteProperty("Value", ConfigValues.Display(Store.GetValue(path))));
            item.Properties.Add(new PSNoteProperty("Source", Store.GetSource(path)));
            item.Properties.Add(new PSNoteProperty("Description", info.Schema?["description"]?.GetValue<string>() ?? string.Empty));
            context.WriteObject(item);
        }

        return 0;
    }

    private int Edit(PickleCommandContext context, bool local, bool profile)
    {
        string file;
        if (profile)
        {
            file = runtime.Paths.ProfileFile;
            if (!File.Exists(file))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, "# Pickle profile: runs at startup (after plugins). Synced by `pk sync`.\n");
            }
        }
        else if (local)
        {
            file = Store.LocalConfigFile;
            if (!File.Exists(file))
            {
                File.WriteAllText(file, "{\n  \"$schema\": \"./config.schema.json\"\n}\n");
            }

            Store.EnsureSchemaReference();
        }
        else
        {
            file = Store.ConfigFile;
            Store.EnsureSchemaReference();
        }

        if (!context.Interactive)
        {
            context.WriteObject(file);
            return 0;
        }

        var opened = EditorLauncher.TryOpen(file, out var message);
        context.WriteHost(message);
        return opened ? 0 : 1;
    }

    private int WritePath(PickleCommandContext context, bool local)
    {
        context.WriteObject(local ? Store.LocalConfigFile : Store.ConfigFile);
        return 0;
    }

    private static int Schema(PickleCommandContext context)
    {
        context.WriteObject(ConfigSchema.SchemaText);
        return 0;
    }

    private static int Unknown(PickleCommandContext context, string sub)
    {
        string[] subcommands = ["get", "set", "reset", "list", "edit", "path", "schema"];
        var hint = Fuzzy.Suggest(sub, subcommands) is [var best, ..] ? $" Did you mean 'pk config {best}'?" : " Run 'pk config --help'.";
        context.WriteError($"Unknown subcommand 'pk config {sub}'.{hint}");
        return 2;
    }
}
