using Pickle.Abstractions;

namespace Pickle.Core.Translation;

/// <summary>
/// Hints printed when a command typed at the prompt isn't found: "did you mean …?" (Damerau-Levenshtein over command
/// names, aliases and PATH executables) and, on Windows, a winget install hint for well-known tools. Installed into
/// <c>$ExecutionContext.InvokeCommand.CommandNotFoundAction</c> by <see cref="TranslationPipeline"/>; the handler calls
/// <c>Invoke-PickleCommandNotFound</c>, which calls <see cref="Handle"/>.
/// </summary>
public sealed class CommandNotFound
{
    /// <summary>Only commands typed by the user (CommandOrigin Runspace) — not lookups inside scripts or modules.</summary>
    public const string HandlerScript = """
        $ExecutionContext.InvokeCommand.CommandNotFoundAction = {
            param($CommandName, $CommandLookupEventArgs)
            if ($CommandLookupEventArgs.CommandOrigin -eq 'Runspace') { Invoke-PickleCommandNotFound -Name $CommandName }
        }
        """;

    public static IReadOnlyDictionary<string, string> KnownWingetIds { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ffmpeg"] = "Gyan.FFmpeg",
        ["ffprobe"] = "Gyan.FFmpeg",
        ["nmap"] = "Insecure.Nmap",
        ["git"] = "Git.Git",
        ["node"] = "OpenJS.NodeJS.LTS",
        ["npm"] = "OpenJS.NodeJS.LTS",
        ["python"] = "Python.Python.3.12",
        ["python3"] = "Python.Python.3.12",
        ["jq"] = "jqlang.jq",
        ["7z"] = "7zip.7zip",
        ["gh"] = "GitHub.cli",
        ["kubectl"] = "Kubernetes.kubectl",
        ["yt-dlp"] = "yt-dlp.yt-dlp",
        ["rg"] = "BurntSushi.ripgrep.MSVC",
        ["fzf"] = "junegunn.fzf",
        ["code"] = "Microsoft.VisualStudioCode",
        ["vim"] = "vim.vim",
        ["docker"] = "Docker.DockerDesktop",
    };

    private readonly PickleRuntime _runtime;
    private readonly object _gate = new();
    private string? _lastHandled;
    private (string Path, DateTime At, List<string> Names)? _pathCache;

    public CommandNotFound(PickleRuntime runtime) => _runtime = runtime;

    /// <summary>Hint lines (plain text) for a missing command.</summary>
    public static IReadOnlyList<string> BuildHints(
        string name,
        IEnumerable<string> candidates,
        bool suggestions,
        bool offerWinget,
        bool isWindows,
        string? wizardWingetId,
        bool hasPickleWinget)
    {
        var hints = new List<string>();
        var tool = NormalizeToolName(name);
        var wingetId = wizardWingetId ?? (KnownWingetIds.TryGetValue(tool, out var known) ? known : null);
        if (offerWinget && isWindows && wingetId is not null)
        {
            var install = hasPickleWinget ? $"pk winget install {wingetId}" : $"winget install --id {wingetId} -e";
            hints.Add($"💡 '{tool}' isn't installed. Install it: {install}");
        }

        if (suggestions)
        {
            var similar = Suggest(name, candidates);
            if (similar.Count > 0)
            {
                hints.Add($"did you mean {string.Join(", ", similar)}?");
            }
        }

        return hints;
    }

    public static IReadOnlyList<string> Suggest(string name, IEnumerable<string> candidates, int max = 3)
    {
        if (name.Length < 2)
        {
            return [];
        }

        var threshold = name.Length <= 4 ? 1 : name.Length <= 8 ? 2 : 3;
        var scored = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrEmpty(candidate) || Math.Abs(candidate.Length - name.Length) > threshold
                || string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var distance = Distance(name, candidate, threshold);
            if (distance <= threshold && (!scored.TryGetValue(candidate, out var existing) || distance < existing))
            {
                scored[candidate] = distance;
            }
        }

        return [.. scored
            .OrderBy(kv => kv.Value)
            .ThenBy(kv => char.ToLowerInvariant(kv.Key[0]) == char.ToLowerInvariant(name[0]) ? 0 : 1)
            .ThenBy(kv => kv.Key.Length)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(kv => kv.Key)];
    }

    /// <summary>Case-insensitive optimal-string-alignment distance (adjacent transpositions count as one edit).</summary>
    public static int Distance(string a, string b, int max = int.MaxValue)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (var j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            var rowMin = int.MaxValue;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                var value = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 2])
                    && char.ToLowerInvariant(a[i - 2]) == char.ToLowerInvariant(b[j - 1]))
                {
                    value = Math.Min(value, d[i - 2, j - 2] + 1);
                }

                d[i, j] = value;
                rowMin = Math.Min(rowMin, value);
            }

            if (rowMin > max)
            {
                return rowMin;
            }
        }

        return d[a.Length, b.Length];
    }

    /// <summary>Called on the pipeline thread by Invoke-PickleCommandNotFound; returns hint lines with ANSI styling.</summary>
    public IReadOnlyList<string> Handle(string name, Func<IEnumerable<string>> sessionCommands)
    {
        var shell = _runtime.Config.Current.Shell;
        if (!shell.CommandNotFoundSuggestions && !shell.OfferWingetInstallForMissingTools)
        {
            return [];
        }

        var currentLine = _runtime.History.Entries.Count > 0 ? _runtime.History.Entries[^1].CommandLine : string.Empty;

        // PowerShell retries a dash-less name as `get-<name>` first; skip that lookup unless the user typed it.
        if (name.StartsWith("get-", StringComparison.OrdinalIgnoreCase) && !name[4..].Contains('-', StringComparison.Ordinal)
            && !currentLine.Contains(name, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var key = name + "\n" + _runtime.History.Entries.Count;
        lock (_gate)
        {
            if (key == _lastHandled)
            {
                return [];
            }

            _lastHandled = key;
        }

        var candidates = shell.CommandNotFoundSuggestions ? sessionCommands().Concat(PathExecutables()) : [];
        var hints = BuildHints(
            name,
            candidates,
            shell.CommandNotFoundSuggestions,
            shell.OfferWingetInstallForMissingTools,
            OperatingSystem.IsWindows(),
            _runtime.WizardRegistry.FindForCommand(name)?.WingetId,
            _runtime.CommandRegistry.Get("winget") is not null);

        var theme = _runtime.Themes.Current;
        return [.. hints.Select(h => h.StartsWith("💡", StringComparison.Ordinal) ? Ansi.Colorize(h, theme.Ui.Info) : Ansi.Dim + h + Ansi.Reset)];
    }

    private static string NormalizeToolName(string name)
    {
        var tool = Path.GetFileName(name);
        return tool.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? tool[..^4] : tool;
    }

    private List<string> PathExecutables()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        lock (_gate)
        {
            if (_pathCache is { } cache && cache.Path == path && DateTime.UtcNow - cache.At < TimeSpan.FromMinutes(1))
            {
                return cache.Names;
            }
        }

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : null;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    var fileName = Path.GetFileName(file);
                    if (extensions is null)
                    {
                        names.Add(fileName);
                    }
                    else if (extensions.FirstOrDefault(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase)) is { } ext)
                    {
                        names.Add(fileName[..^ext.Length]);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
        }

        var list = names.ToList();
        lock (_gate)
        {
            _pathCache = (path, DateTime.UtcNow, list);
        }

        return list;
    }
}
