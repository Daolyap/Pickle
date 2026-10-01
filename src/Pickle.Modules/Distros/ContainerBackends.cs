using System.Text.Json;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Modules.Distros;

/// <summary>Distrobox: any distribution in a container that shares your home (<c>distrobox list</c>, <c>enter</c>, <c>stop</c>, <c>rm</c>).</summary>
public sealed class DistroboxBackend(Func<IProgramRunner> runnerFactory) : IDistroBackend
{
    private const string Program = "distrobox";

    private IProgramRunner Runner => runnerFactory();

    public string Id => "distrobox";

    public string DisplayName => "Distrobox";

    public bool IsAvailable => Runner.Find(Program) is not null;

    public async Task<IReadOnlyList<Distro>> ListAsync(CancellationToken cancellationToken)
    {
        var result = await Runner.RunAsync(Program, ["list", "--no-color"], new ProgramRunOptions { Timeout = TimeSpan.FromSeconds(30) }, cancellationToken).ConfigureAwait(false);
        return result.Success ? Parse(result.StdOut) : throw new InvalidOperationException(result.Message);
    }

    public BackendCommand? Command(Distro distro, string verb)
    {
        var name = DistroNames.Require(distro.Name);
        return verb switch
        {
            // There is no `distrobox start`: entering a container boots it, and `true` leaves again at once.
            "start" => new BackendCommand(Program, ["enter", name, "--", "true"], TimeSpan.FromMinutes(5)),
            "stop" => new BackendCommand(Program, ["stop", "--yes", name], TimeSpan.FromMinutes(2)),
            "remove" => new BackendCommand(Program, ["rm", "--force", name], TimeSpan.FromMinutes(2)),
            "upgrade" => new BackendCommand(Program, ["upgrade", name], TimeSpan.FromMinutes(30)),
            _ => null,
        };
    }

    public string? ShellLine(Distro distro, string what) =>
        what == "enter" ? PowerShellQuote.Command(Program, "enter", DistroNames.Require(distro.Name)) : null;

    public string CreateLine(string name, string? image) =>
        PowerShellQuote.Command(Program, [.. new[] { "create", "--yes", "--name", DistroNames.Require(name) }, .. image is null ? Array.Empty<string>() : ["--image", DistroNames.RequireImage(image)]]);

    /// <summary><c>ID | NAME | STATUS | IMAGE</c> rows.</summary>
    internal static IReadOnlyList<Distro> Parse(string text)
    {
        var list = new List<Distro>();
        foreach (var line in DistroNames.Lines(text))
        {
            var cells = line.Split('|', StringSplitOptions.TrimEntries);
            if (cells.Length < 4 || cells[0].Equals("ID", StringComparison.OrdinalIgnoreCase) || !DistroNames.IsValid(cells[1]))
            {
                continue;
            }

            var status = cells[2];
            var state = status.StartsWith("Up", StringComparison.OrdinalIgnoreCase) ? "running" : status.StartsWith("Exited", StringComparison.OrdinalIgnoreCase) ? "stopped" : status.ToLowerInvariant();
            list.Add(new Distro("distrobox", cells[1], state, cells[3], false,
            [
                $"Name:   {cells[1]}",
                $"State:  {status}",
                $"Image:  {cells[3]}",
                $"Id:     {cells[0]}",
            ]));
        }

        return [.. list.OrderByDescending(d => d.IsRunning).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)];
    }
}

/// <summary>Toolbx (Fedora Silverblue and friends): <c>toolbox</c> creates and enters, Podman lists and stops (the toolbox label marks them).</summary>
public sealed class ToolboxBackend(Func<IProgramRunner> runnerFactory) : IDistroBackend
{
    private const string Program = "toolbox";
    private const string Podman = "podman";

    private IProgramRunner Runner => runnerFactory();

    public string Id => "toolbox";

    public string DisplayName => "Toolbx";

    public bool IsAvailable => Runner.Find(Program) is not null && Runner.Find(Podman) is not null;

    public async Task<IReadOnlyList<Distro>> ListAsync(CancellationToken cancellationToken)
    {
        var result = await Runner.RunAsync(
            Podman,
            ["ps", "-a", "--filter", "label=com.github.containers.toolbox=true", "--format", "json"],
            new ProgramRunOptions { Timeout = TimeSpan.FromSeconds(30) },
            cancellationToken).ConfigureAwait(false);
        return result.Success ? Parse(result.StdOut) : throw new InvalidOperationException(result.Message);
    }

    public BackendCommand? Command(Distro distro, string verb)
    {
        var name = DistroNames.Require(distro.Name);
        return verb switch
        {
            "start" or "stop" or "restart" => new BackendCommand(Podman, [verb, name], TimeSpan.FromMinutes(2)),
            "remove" => new BackendCommand(Program, ["rm", "--force", name], TimeSpan.FromMinutes(2)),
            _ => null,
        };
    }

    public string? ShellLine(Distro distro, string what) =>
        what == "enter" ? PowerShellQuote.Command(Program, "enter", DistroNames.Require(distro.Name)) : null;

    public string CreateLine(string name, string? image) =>
        PowerShellQuote.Command(Program, [.. new[] { "create" }, .. image is null ? Array.Empty<string>() : ["--image", DistroNames.RequireImage(image)], DistroNames.Require(name)]);

    /// <summary><c>podman ps --format json</c>: an array of objects with <c>Names</c> (an array), <c>State</c>, <c>Image</c>.</summary>
    internal static IReadOnlyList<Distro> Parse(string json)
    {
        if (ModuleJson.Document(json) is not { ValueKind: JsonValueKind.Array } array)
        {
            return [];
        }

        var list = new List<Distro>();
        foreach (var item in array.EnumerateArray())
        {
            var names = item.Child("Names");
            var name = names is { ValueKind: JsonValueKind.Array } n ? n.EnumerateArray().Select(e => e.GetString()).FirstOrDefault() : names?.ToString();
            if (!DistroNames.IsValid(name))
            {
                continue;
            }

            var state = item.Text("State").ToLowerInvariant() switch { "exited" or "stopped" => "stopped", var other => other };
            var image = item.Text("Image");
            list.Add(new Distro("toolbox", name!, state, image, false,
            [
                $"Name:   {name}",
                $"State:  {state}",
                $"Image:  {image}",
                $"Id:     {item.Text("Id")[..Math.Min(12, item.Text("Id").Length)]}",
            ]));
        }

        return [.. list.OrderByDescending(d => d.IsRunning).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)];
    }
}

/// <summary>LXC / LXD (<c>lxc</c>) and Incus (<c>incus</c>) system containers and VMs; both CLIs print the same JSON.</summary>
public sealed class LxcBackend(Func<IProgramRunner> runnerFactory, string program = "lxc") : IDistroBackend
{
    private IProgramRunner Runner => runnerFactory();

    public string Id => program;

    public string DisplayName => program == "incus" ? "Incus" : "LXC";

    public bool IsAvailable => Runner.Find(program) is not null;

    public async Task<IReadOnlyList<Distro>> ListAsync(CancellationToken cancellationToken)
    {
        var result = await Runner.RunAsync(program, ["list", "--format", "json"], new ProgramRunOptions { Timeout = TimeSpan.FromSeconds(30) }, cancellationToken).ConfigureAwait(false);
        return result.Success ? Parse(result.StdOut, program) : throw new InvalidOperationException(result.Message);
    }

    public BackendCommand? Command(Distro distro, string verb)
    {
        var name = DistroNames.Require(distro.Name);
        return verb switch
        {
            "start" or "stop" or "restart" => new BackendCommand(program, [verb, name], TimeSpan.FromMinutes(2)),
            "remove" => new BackendCommand(program, ["delete", "--force", name], TimeSpan.FromMinutes(2)),
            _ => null,
        };
    }

    public string? ShellLine(Distro distro, string what) =>
        what == "enter" ? PowerShellQuote.Command(program, "exec", DistroNames.Require(distro.Name), "--", "sh", "-c", "exec bash -l 2>/dev/null || exec sh -l") : null;

    public string CreateLine(string name, string? image) =>
        PowerShellQuote.Command(program, "launch", image is null ? throw new ArgumentException($"{DisplayName} needs an image: --image ubuntu:24.04 (or images:debian/12).") : DistroNames.RequireImage(image), DistroNames.Require(name));

    internal static IReadOnlyList<Distro> Parse(string json, string backend)
    {
        if (ModuleJson.Document(json) is not { ValueKind: JsonValueKind.Array } array)
        {
            return [];
        }

        var list = new List<Distro>();
        foreach (var item in array.EnumerateArray())
        {
            var name = item.Text("name");
            if (!DistroNames.IsValid(name))
            {
                continue;
            }

            var state = item.Text("status").ToLowerInvariant();
            var kind = item.Text("type", "container");
            var image = item.Child("config") is { } config ? config.Text("image.description", config.Text("image.os")) : string.Empty;
            var addresses = item.Child("state")?.Child("network") is { ValueKind: JsonValueKind.Object } network
                ? network.EnumerateObject()
                    .Where(n => !n.Name.Equals("lo", StringComparison.Ordinal))
                    .SelectMany(n => n.Value.Items("addresses"))
                    .Where(a => a.Text("family") == "inet")
                    .Select(a => a.Text("address"))
                    .ToList()
                : [];
            var lines = new List<string> { $"Name:     {name}", $"State:    {state}", $"Type:     {kind}", $"Arch:     {item.Text("architecture")}" };
            if (image.Length > 0)
            {
                lines.Add($"Image:    {image}");
            }

            if (addresses.Count > 0)
            {
                lines.Add($"Address:  {string.Join(", ", addresses)}");
            }

            list.Add(new Distro(backend, name, state, kind + (image.Length > 0 ? " · " + image : string.Empty), false, lines));
        }

        return [.. list.OrderByDescending(d => d.IsRunning).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)];
    }
}
