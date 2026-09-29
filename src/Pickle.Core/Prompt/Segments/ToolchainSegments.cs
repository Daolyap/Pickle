using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pickle.Abstractions;

namespace Pickle.Core.Prompt.Segments;

/// <summary>.NET SDK version inside .NET projects: global.json's pinned SDK, else the installed one.</summary>
public sealed class DotnetSegment(SegmentEnvironment environment) : IPromptSegment
{
    public string Type => "dotnet";

    public async ValueTask<PromptSegmentOutput?> RenderAsync(PromptContext context, SegmentStyle style, CancellationToken cancellationToken) =>
        await environment.GetDotnetVersion(context.Cwd, cancellationToken).ConfigureAwait(false) is { Length: > 0 } version
            ? new PromptSegmentOutput(SegmentText.Sanitize(version))
            : null;
}

/// <summary>Go version inside modules, from go.mod's toolchain or go directive (no process started).</summary>
public sealed class GoSegment(SegmentEnvironment environment) : IPromptSegment
{
    public string Type => "go";

    public ValueTask<PromptSegmentOutput?> RenderAsync(PromptContext context, SegmentStyle style, CancellationToken cancellationToken) =>
        environment.GetGoVersion(context.Cwd) is { Length: > 0 } version ? SegmentText.Show(SegmentText.Sanitize(version)) : SegmentText.Hide();
}

/// <summary>Rust inside Cargo projects: the rust-toolchain channel, else the installed rustc's version.</summary>
public sealed class RustSegment(SegmentEnvironment environment) : IPromptSegment
{
    public string Type => "rust";

    public async ValueTask<PromptSegmentOutput?> RenderAsync(PromptContext context, SegmentStyle style, CancellationToken cancellationToken) =>
        await environment.GetRustVersion(context.Cwd, cancellationToken).ConfigureAwait(false) is { Length: > 0 } version
            ? new PromptSegmentOutput(SegmentText.Sanitize(version))
            : null;
}

/// <summary>Finds the project files the toolchain segments need and reads versions from them.</summary>
public static partial class Toolchains
{
    private static readonly string[] DotnetProjectExtensions = [".csproj", ".fsproj", ".vbproj", ".sln", ".slnx"];

    /// <summary>A .NET project here: a project or solution file in the directory, or a global.json at or above it.</summary>
    public static bool IsDotnetProject(string cwd)
    {
        try
        {
            if (Directory.EnumerateFiles(cwd).Any(f => DotnetProjectExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            return false;
        }

        return SegmentEnvironment.FindUpwards(cwd, "global.json", directory: false) is not null;
    }

    /// <summary>The SDK version global.json pins ({"sdk": {"version": "…"}}), if any.</summary>
    public static string? ParseGlobalJson(string json)
    {
        try
        {
            return JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })?["sdk"]?["version"] is JsonValue v
                && v.TryGetValue<string>(out var version) ? version : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>go.mod's <c>toolchain go1.22.3</c> (preferred) or <c>go 1.22</c> line.</summary>
    public static string? ParseGoMod(string text)
    {
        string? go = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (ToolchainLine().Match(line) is { Success: true } toolchain)
            {
                return toolchain.Groups[1].Value;
            }

            if (GoLine().Match(line) is { Success: true } directive)
            {
                go ??= directive.Groups[1].Value;
            }
        }

        return go;
    }

    /// <summary>The channel of a rust-toolchain.toml (<c>channel = "1.80"</c>) or a plain rust-toolchain file.</summary>
    public static string? ParseRustToolchain(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (ChannelLine().Match(line) is { Success: true } channel)
            {
                return channel.Groups[1].Value;
            }
        }

        var plain = text.Trim();
        return plain.Length is > 0 and < 40 && !plain.Contains('[', StringComparison.Ordinal) && !plain.Contains('\n', StringComparison.Ordinal) ? plain : null;
    }

    /// <summary><c>rustc 1.80.1 (3f5fd8dd4 2024-08-06)</c> → 1.80.1.</summary>
    public static string? ParseRustc(string output) => RustcLine().Match(output) is { Success: true } m ? m.Groups[1].Value : null;

    [GeneratedRegex(@"^toolchain\s+go(\d+(?:\.\d+)*)")]
    private static partial Regex ToolchainLine();

    [GeneratedRegex(@"^go\s+(\d+(?:\.\d+)*)\s*$")]
    private static partial Regex GoLine();

    [GeneratedRegex(@"^channel\s*=\s*[""']([^""']+)[""']")]
    private static partial Regex ChannelLine();

    [GeneratedRegex(@"rustc\s+(\d+\.\d+\.\d+\S*)")]
    private static partial Regex RustcLine();
}

/// <summary>Live toolchain versions: project files first, then (once per session) the installed tool.</summary>
public sealed class ToolchainReader
{
    private readonly CachedFile<string> _globalJson = new(Toolchains.ParseGlobalJson);
    private readonly CachedFile<string> _goMod = new(Toolchains.ParseGoMod);
    private readonly CachedFile<string> _rustToolchain = new(Toolchains.ParseRustToolchain);
    private readonly NodeVersionProbe _dotnet = new("dotnet");
    private readonly NodeVersionProbe _rustc = new("rustc", parse: Toolchains.ParseRustc);

    public Task<string?> DotnetAsync(string cwd, CancellationToken cancellationToken)
    {
        if (!Toolchains.IsDotnetProject(cwd))
        {
            return Task.FromResult<string?>(null);
        }

        return SegmentEnvironment.FindUpwards(cwd, "global.json", directory: false) is { } dir && _globalJson.Read(Path.Combine(dir, "global.json")) is { } pinned
            ? Task.FromResult<string?>(pinned)
            : _dotnet.GetVersionAsync(cancellationToken);
    }

    public string? Go(string cwd) =>
        SegmentEnvironment.FindUpwards(cwd, "go.mod", directory: false) is { } dir ? _goMod.Read(Path.Combine(dir, "go.mod")) : null;

    public Task<string?> RustAsync(string cwd, CancellationToken cancellationToken)
    {
        if (SegmentEnvironment.FindUpwards(cwd, "Cargo.toml", directory: false) is not { } project)
        {
            return Task.FromResult<string?>(null);
        }

        foreach (var name in new[] { "rust-toolchain.toml", "rust-toolchain" })
        {
            if (SegmentEnvironment.FindUpwards(project, name, directory: false) is { } dir && _rustToolchain.Read(Path.Combine(dir, name)) is { } channel)
            {
                return Task.FromResult<string?>(channel);
            }
        }

        return _rustc.GetVersionAsync(cancellationToken);
    }
}
