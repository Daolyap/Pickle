using System.Text.RegularExpressions;

namespace Pickle.Modules.Distros;

/// <summary>A WSL distribution, a Distrobox or Toolbx container, or an LXC / Incus instance.</summary>
/// <param name="Backend">The <see cref="IDistroBackend.Id"/> that owns it.</param>
/// <param name="State"><c>running</c>, <c>stopped</c>, or whatever the tool reports (<c>created</c>, <c>frozen</c>).</param>
public sealed record Distro(string Backend, string Name, string State, string Summary, bool IsDefault, IReadOnlyList<string> Lines)
{
    public bool IsRunning => State == "running";
}

/// <summary>A program and its arguments (never a shell string) for one lifecycle action.</summary>
public sealed record BackendCommand(string Program, IReadOnlyList<string> Arguments, TimeSpan? Timeout = null)
{
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }
}

/// <summary>One way of running Linux environments next to the shell. Implementations only build arguments and parse output.</summary>
public interface IDistroBackend
{
    /// <summary>Lowercase and stable: <c>wsl</c>, <c>distrobox</c>, <c>toolbox</c>, <c>lxc</c>, <c>incus</c>. What <c>--backend</c> takes.</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>The tool is on PATH.</summary>
    bool IsAvailable { get; }

    Task<IReadOnlyList<Distro>> ListAsync(CancellationToken cancellationToken);

    /// <summary>The details pane's lines; may ask the tool for more than the list had.</summary>
    Task<IReadOnlyList<string>> DescribeAsync(Distro distro, CancellationToken cancellationToken) => Task.FromResult(distro.Lines);

    /// <summary><c>start</c>, <c>stop</c>, <c>restart</c>, <c>remove</c>, <c>default</c> or <c>upgrade</c>; null when the tool has nothing like it.</summary>
    BackendCommand? Command(Distro distro, string verb);

    /// <summary>The PowerShell line for something that needs the terminal: <c>enter</c> (a shell inside it) or <c>export</c>. Null when not applicable.</summary>
    string? ShellLine(Distro distro, string what);

    /// <summary>The PowerShell line that creates (or installs) one. Throws <see cref="ArgumentException"/> when the tool needs an image and none was given.</summary>
    string CreateLine(string name, string? image);
}

public static partial class DistroNames
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z")]
    private static partial Regex Name();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:/@+-]{0,199}\z")]
    private static partial Regex Image();

    public static bool IsValid(string? name) => name is not null && Name().IsMatch(name);

    /// <summary>Names reach other programs as arguments; one that starts with a dash would be read as an option.</summary>
    public static string Require(string? name) =>
        IsValid(name) ? name! : throw new ArgumentException($"'{name}' is not a usable name: letters, digits, '.', '_' and '-', starting with a letter or digit.");

    public static string RequireImage(string? image) =>
        image is not null && Image().IsMatch(image) ? image : throw new ArgumentException($"'{image}' is not a usable image reference (like ubuntu:24.04 or docker.io/library/alpine).");

    /// <summary>wsl.exe writes UTF-16 when its output is redirected: what arrives is full of NULs, and maybe a byte-order mark.</summary>
    public static string Clean(string text) =>
        text.Replace("\0", string.Empty, StringComparison.Ordinal).Replace("�", string.Empty, StringComparison.Ordinal).Replace("﻿", string.Empty, StringComparison.Ordinal).Replace("\r", string.Empty, StringComparison.Ordinal);

    public static string[] Lines(string text) => Clean(text).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
