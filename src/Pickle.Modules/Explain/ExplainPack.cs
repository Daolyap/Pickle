using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pickle.Modules.Explain;

public sealed class WarningRule
{
    public string Pattern { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;
}

/// <summary>What the pack knows about one command: a summary, its flags (a value starting with "=" means the flag takes a value), and subcommands.</summary>
public sealed class CommandEntry
{
    public string Summary { get; set; } = string.Empty;

    public Dictionary<string, string> Flags { get; set; } = new(StringComparer.Ordinal);

    /// <summary>What the positional arguments are.</summary>
    public string? Args { get; set; }

    public Dictionary<string, CommandEntry> Subcommands { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Another command follows (sudo, xargs): explain that too.</summary>
    public bool Wraps { get; set; }

    public bool CombineShortFlags { get; set; } = true;

    public List<WarningRule> Warnings { get; set; } = [];
}

/// <summary>The offline knowledge behind <c>pk explain</c>: embedded <c>pack.json</c>, plus any <c>explain/*.json</c> in the data folder (yours extend and replace it).</summary>
public sealed class ExplainPack(IReadOnlyDictionary<string, CommandEntry> commands)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public IReadOnlyDictionary<string, CommandEntry> Commands { get; } = commands;

    public CommandEntry? Find(string name) =>
        Commands.TryGetValue(name, out var entry) ? entry : Commands.TryGetValue(Path.GetFileNameWithoutExtension(name), out entry) ? entry : null;

    public static ExplainPack Load(string? userDirectory = null, Action<string, Exception>? onError = null)
    {
        var commands = new Dictionary<string, CommandEntry>(StringComparer.OrdinalIgnoreCase);
        using (var stream = typeof(ExplainPack).Assembly.GetManifestResourceStream("Pickle.Modules.Explain.pack.json"))
        {
            if (stream is not null)
            {
                Merge(commands, JsonSerializer.Deserialize<Dictionary<string, CommandEntry>>(stream, Json) ?? []);
            }
        }

        if (userDirectory is not null && Directory.Exists(userDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(userDirectory, "*.json").Order(StringComparer.Ordinal))
            {
                try
                {
                    Merge(commands, JsonSerializer.Deserialize<Dictionary<string, CommandEntry>>(File.ReadAllText(file), Json) ?? []);
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    onError?.Invoke(file, ex);
                }
            }
        }

        return new ExplainPack(commands);
    }

    private static void Merge(Dictionary<string, CommandEntry> into, Dictionary<string, CommandEntry> from)
    {
        foreach (var (name, entry) in from)
        {
            into[name] = entry;
        }
    }
}
