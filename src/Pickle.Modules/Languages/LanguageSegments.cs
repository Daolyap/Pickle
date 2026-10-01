using System.Text.RegularExpressions;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Modules.Languages;

/// <summary>How one language is detected and read: project files that mark it, version files that pin it, and the tool that reports it.</summary>
public sealed record LanguageSpec(string Type, string Name, IReadOnlyList<string> Tools, IReadOnlyList<string> VersionArguments, IReadOnlyList<string> Markers, IReadOnlyList<string> VersionFiles, bool VersionOnStandardError = false, string? ToolVersionsKey = null);

/// <summary>
/// Prompt segments for the languages Pickle's built-in segments do not cover (node, .NET, Go, Rust and Python virtual
/// environments already exist). A segment shows inside a project that has one of its marker files, using the version a
/// pin file names, else <c>&lt;tool&gt; --version</c> (asked once per session, never from the prompt thread).
/// </summary>
public static partial class LanguageSpecs
{
    public static IReadOnlyList<LanguageSpec> All { get; } =
    [
        new("python", "Python", ["python3", "python"], ["--version"], ["pyproject.toml", "requirements.txt", "setup.py", "setup.cfg", "Pipfile", ".python-version", "tox.ini"], [".python-version"], ToolVersionsKey: "python"),
        new("java", "Java", ["java"], ["-version"], ["pom.xml", "build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts", ".java-version"], [".java-version"], VersionOnStandardError: true, ToolVersionsKey: "java"),
        new("ruby", "Ruby", ["ruby"], ["--version"], ["Gemfile", "Rakefile", "*.gemspec", ".ruby-version"], [".ruby-version"], ToolVersionsKey: "ruby"),
        new("php", "PHP", ["php"], ["--version"], ["composer.json", ".php-version"], [".php-version"], ToolVersionsKey: "php"),
        new("deno", "Deno", ["deno"], ["--version"], ["deno.json", "deno.jsonc", "deno.lock"], []),
        new("bun", "Bun", ["bun"], ["--version"], ["bun.lockb", "bun.lock", "bunfig.toml"], []),
        new("terraform", "Terraform", ["terraform", "tofu"], ["version"], ["*.tf", ".terraform", ".terraform-version"], [".terraform-version"], ToolVersionsKey: "terraform"),
        new("elixir", "Elixir", ["elixir"], ["-e", "IO.puts(System.version())"], ["mix.exs"], [], ToolVersionsKey: "elixir"),
        new("lua", "Lua", ["lua", "luajit"], ["-v"], [".luarc.json", "stylua.toml", "*.rockspec"], [], ToolVersionsKey: "lua"),
        new("swift", "Swift", ["swift"], ["--version"], ["Package.swift"], []),
        new("zig", "Zig", ["zig"], ["version"], ["build.zig", "build.zig.zon"], []),
        new("dart", "Dart", ["dart"], ["--version"], ["pubspec.yaml"], []),
    ];

    /// <summary>The first version-looking token of a tool's output: <c>Python 3.12.1</c> → 3.12.1, <c>openjdk version "21.0.1"</c> → 21.0.1, <c>Terraform v1.7.0</c> → 1.7.0.</summary>
    public static string? ParseVersion(string output)
    {
        var match = VersionToken().Match(output);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>The version a pin file names: a plain <c>3.12.1</c> / <c>v1.7.0</c> line, or a <c>.tool-versions</c> line <c>python 3.12.1</c>.</summary>
    public static string? ParsePinned(string content, string? toolVersionsKey = null)
    {
        foreach (var raw in content.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (toolVersionsKey is not null && line.StartsWith(toolVersionsKey + " ", StringComparison.Ordinal))
            {
                return ParseVersion(line[(toolVersionsKey.Length + 1)..]);
            }

            if (toolVersionsKey is null || !line.Contains(' ', StringComparison.Ordinal))
            {
                return PlainVersion().Match(line) is { Success: true } m ? m.Groups[1].Value : null;
            }
        }

        return null;
    }

    [GeneratedRegex(@"(?<![\d.])(\d+\.\d+(?:\.\d+)?(?:[-+.][0-9A-Za-z]+)*)")]
    private static partial Regex VersionToken();

    [GeneratedRegex(@"^v?(\d+(?:\.\d+){0,3}(?:[-+.][0-9A-Za-z]+)*)$")]
    private static partial Regex PlainVersion();
}

internal sealed class LanguageSegment(LanguageSpec spec, Func<IProgramRunner> runner) : IPromptSegment
{
    private readonly Lazy<Task<string?>> _installed = new(() => Task.Run(() => AskAsync(spec, runner())));

    public string Type => spec.Type;

    public async ValueTask<PromptSegmentOutput?> RenderAsync(PromptContext context, SegmentStyle style, CancellationToken cancellationToken)
    {
        if (FindProject(context.Cwd, spec) is not { } project)
        {
            return null;
        }

        var version = ReadPinned(project, spec) ?? await _installed.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrEmpty(version) ? null : new PromptSegmentOutput(version);
    }

    /// <summary>The folder (at or above <paramref name="cwd"/>) that holds one of the spec's marker files, or null.</summary>
    internal static string? FindProject(string cwd, LanguageSpec spec)
    {
        if (string.IsNullOrEmpty(cwd))
        {
            return null;
        }

        try
        {
            for (var directory = new DirectoryInfo(cwd); directory is not null; directory = directory.Parent)
            {
                if (spec.Markers.Any(m => m.Contains('*', StringComparison.Ordinal)
                    ? directory.EnumerateFileSystemInfos(m).Any()
                    : File.Exists(Path.Combine(directory.FullName, m)) || Directory.Exists(Path.Combine(directory.FullName, m))))
                {
                    return directory.FullName;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }

        return null;
    }

    internal static string? ReadPinned(string project, LanguageSpec spec)
    {
        try
        {
            foreach (var file in spec.VersionFiles)
            {
                var path = Path.Combine(project, file);
                if (File.Exists(path) && LanguageSpecs.ParsePinned(File.ReadAllText(path)) is { } pinned)
                {
                    return pinned;
                }
            }

            var toolVersions = Path.Combine(project, ".tool-versions");
            if (spec.ToolVersionsKey is { } key && File.Exists(toolVersions))
            {
                return LanguageSpecs.ParsePinned(File.ReadAllText(toolVersions), key);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static async Task<string?> AskAsync(LanguageSpec spec, IProgramRunner runner)
    {
        foreach (var tool in spec.Tools)
        {
            if (runner.Find(tool) is null)
            {
                continue;
            }

            var result = await runner.RunAsync(tool, spec.VersionArguments, new ProgramRunOptions { Timeout = TimeSpan.FromSeconds(3) }).ConfigureAwait(false);
            var text = spec.VersionOnStandardError ? result.StdErr + result.StdOut : result.StdOut + result.StdErr;
            if (LanguageSpecs.ParseVersion(text) is { } version)
            {
                return version;
            }
        }

        return null;
    }
}

public sealed class LanguagesModule : IPicklePlugin
{
    public const string ModuleId = "languages";

    public static ModuleDescriptor Descriptor { get; } = new()
    {
        Id = ModuleId,
        Name = "Language versions",
        Description = "Prompt segments for python, java, ruby, php, deno, bun, terraform, elixir, lua, swift, zig and dart",
        Create = () => new LanguagesModule(),
        Provides = [.. LanguageSpecs.All.Select(s => "segment: " + s.Type)],
    };

    string IPicklePlugin.Id => "pickle." + ModuleId;

    public string DisplayName => Descriptor.Name;

    public string Description => Descriptor.Description;

    public void Initialize(IPickleContext context)
    {
        foreach (var spec in LanguageSpecs.All)
        {
            context.PromptSegments.Register(new LanguageSegment(spec, () => context.Services.Require<IProgramRunner>()));
        }
    }
}
