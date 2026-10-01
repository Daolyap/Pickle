using System.Text.RegularExpressions;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Modules.Docker;

public enum DockerKind
{
    Containers,
    Images,
    Volumes,
    Compose,
}

/// <summary>A container, image, volume or compose project as the panel and <c>pk docker</c> show it.</summary>
public sealed record DockerItem(DockerKind Kind, string Id, string Name, string Summary, string State, IReadOnlyList<string> Lines)
{
    public bool IsRunning => State is "running" or "restarting" or "paused";
}

/// <summary>Stored under <c>extensions.docker</c>: <c>pk config set extensions.docker.runtime podman</c>.</summary>
public sealed class DockerSettings
{
    /// <summary>"auto" (docker, else podman), "docker" or "podman".</summary>
    public string Runtime { get; set; } = "auto";
}

/// <summary>Docker or Podman through their CLI (same <c>--format json</c> and verbs), always as argument lists.</summary>
public sealed partial class DockerService(IProgramRunner runner, IConfigStore config)
{
    private static readonly string[] Runtimes = ["docker", "podman"];

    public string? Runtime
    {
        get
        {
            var wanted = config.Get<DockerSettings>(DockerModule.ModuleId).Runtime;
            return Runtimes.Contains(wanted, StringComparer.OrdinalIgnoreCase) ? (runner.Find(wanted) is not null ? wanted.ToLowerInvariant() : null) : Runtimes.FirstOrDefault(r => runner.Find(r) is not null);
        }
    }

    public async Task<IReadOnlyList<DockerItem>> ListAsync(DockerKind kind, CancellationToken cancellationToken)
    {
        var runtime = Runtime ?? throw new InvalidOperationException("Neither docker nor podman was found on PATH.");
        string[] arguments = kind switch
        {
            DockerKind.Containers => ["ps", "-a", "--no-trunc", "--format", "{{json .}}"],
            DockerKind.Images => ["images", "--format", "{{json .}}"],
            DockerKind.Volumes => ["volume", "ls", "--format", "{{json .}}"],
            _ => ["compose", "ls", "-a", "--format", "json"],
        };
        var result = await runner.RunAsync(runtime, arguments, new ProgramRunOptions { Timeout = TimeSpan.FromSeconds(30) }, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Message);
        }

        return kind switch
        {
            DockerKind.Containers => ParseContainers(result.StdOut),
            DockerKind.Images => ParseImages(result.StdOut),
            DockerKind.Volumes => ParseVolumes(result.StdOut),
            _ => ParseCompose(result.StdOut),
        };
    }

    public async Task<IReadOnlyList<string>> DescribeAsync(DockerItem item, CancellationToken cancellationToken)
    {
        if (item.Kind == DockerKind.Compose || Runtime is not { } runtime)
        {
            return item.Lines;
        }

        string[] arguments = item.Kind switch
        {
            DockerKind.Containers => ["logs", "--tail", "20", item.Id],
            DockerKind.Images => ["history", item.Id],
            _ => ["volume", "inspect", item.Id],
        };
        var result = await runner.RunAsync(runtime, arguments, new ProgramRunOptions { Timeout = TimeSpan.FromSeconds(15) }, cancellationToken).ConfigureAwait(false);
        var extra = (result.StdOut + result.StdErr).Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(30);
        return [.. item.Lines, string.Empty, item.Kind == DockerKind.Containers ? "Last log lines:" : "Details:", .. extra];
    }

    /// <summary>One action on an item; <paramref name="verb"/> is start, stop, restart, remove, pause or unpause (containers), remove (images and volumes), up, down or restart (compose).</summary>
    public async Task<ServiceOperationResult> ActAsync(DockerItem item, string verb, CancellationToken cancellationToken)
    {
        var runtime = Runtime ?? throw new InvalidOperationException("Neither docker nor podman was found on PATH.");
        var arguments = Arguments(item, verb);
        var result = await runner.RunAsync(runtime, arguments, new ProgramRunOptions { Timeout = TimeSpan.FromMinutes(5) }, cancellationToken).ConfigureAwait(false);
        return new ServiceOperationResult(result.Success, result.Success ? $"{verb} {item.Name}: done." : result.Message);
    }

    public IReadOnlyList<string> Arguments(DockerItem item, string verb)
    {
        Require(item);
        return (item.Kind, verb) switch
        {
            (DockerKind.Containers, "start" or "stop" or "restart" or "pause" or "unpause") => [verb, item.Id],
            (DockerKind.Containers, "remove") => ["rm", item.Id],
            (DockerKind.Images, "remove") => ["rmi", item.Id],
            (DockerKind.Volumes, "remove") => ["volume", "rm", item.Id],
            (DockerKind.Compose, "up") => ["compose", "-p", item.Id, "up", "-d"],
            (DockerKind.Compose, "down" or "restart" or "stop" or "start") => ["compose", "-p", item.Id, verb],
            _ => throw new ArgumentException($"'{verb}' is not something to do with a {item.Kind.ToString().TrimEnd('s').ToLowerInvariant()}."),
        };
    }

    /// <summary>The PowerShell line for something that streams or needs a terminal (logs, a shell in a container, compose with output).</summary>
    public string? ShellCommand(DockerItem item, string what)
    {
        if (Runtime is not { } runtime)
        {
            return null;
        }

        Require(item);
        return (item.Kind, what) switch
        {
            (DockerKind.Containers, "logs") => PowerShellQuote.Command(runtime, "logs", "-f", "--tail", "200", item.Id),
            (DockerKind.Containers, "shell") => PowerShellQuote.Command(runtime, "exec", "-it", item.Id, "sh"),
            (DockerKind.Containers, "stats") => PowerShellQuote.Command(runtime, "stats", item.Id),
            (DockerKind.Images, "run") => PowerShellQuote.Command(runtime, "run", "--rm", "-it", item.Id),
            (DockerKind.Compose, "logs") => PowerShellQuote.Command(runtime, "compose", "-p", item.Id, "logs", "-f", "--tail", "100"),
            _ => null,
        };
    }

    public static IReadOnlyList<DockerItem> ParseContainers(string output) =>
        [.. ModuleJson.Lines(output).Select(j =>
        {
            var state = j.Text("State", "unknown").ToLowerInvariant();
            var name = j.Text("Names").Split(',')[0];
            var id = j.Text("ID");
            return new DockerItem(DockerKind.Containers, id.Length > 12 ? id[..12] : id, name, $"{j.Text("Image")} · {j.Text("Status")}", state,
            [
                $"Name:    {name}",
                $"Image:   {j.Text("Image")}",
                $"State:   {j.Text("Status")}",
                $"Ports:   {j.Text("Ports")}",
                $"Created: {j.Text("CreatedAt")}",
                $"Command: {j.Text("Command")}",
            ]);
        }).Where(i => i.Id.Length > 0).OrderByDescending(i => i.IsRunning).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)];

    public static IReadOnlyList<DockerItem> ParseImages(string output) =>
        [.. ModuleJson.Lines(output).Select(j =>
        {
            var name = $"{j.Text("Repository", "<none>")}:{j.Text("Tag", "<none>")}";
            return new DockerItem(DockerKind.Images, j.Text("ID"), name, $"{j.Text("Size")} · {j.Text("CreatedSince")}", "image",
            [
                $"Image:   {name}",
                $"Id:      {j.Text("ID")}",
                $"Size:    {j.Text("Size")}",
                $"Created: {j.Text("CreatedSince")}",
            ]);
        }).Where(i => i.Id.Length > 0).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)];

    public static IReadOnlyList<DockerItem> ParseVolumes(string output) =>
        [.. ModuleJson.Lines(output).Select(j => new DockerItem(DockerKind.Volumes, j.Text("Name"), j.Text("Name"), j.Text("Driver"), "volume", [$"Volume: {j.Text("Name")}", $"Driver: {j.Text("Driver")}", $"Mount:  {j.Text("Mountpoint")}"]))
            .Where(i => i.Id.Length > 0).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)];

    // `docker compose ls --format json` prints one JSON array.
    public static IReadOnlyList<DockerItem> ParseCompose(string output)
    {
        var projects = new List<DockerItem>();
        if (ModuleJson.Document(output) is not { ValueKind: System.Text.Json.JsonValueKind.Array } array)
        {
            return projects;
        }

        foreach (var j in array.EnumerateArray())
        {
            var name = j.Text("Name");
            var status = j.Text("Status");
            projects.Add(new DockerItem(DockerKind.Compose, name, name, status, status.StartsWith("running", StringComparison.OrdinalIgnoreCase) ? "running" : "stopped", [$"Project: {name}", $"Status:  {status}", $"Files:   {j.Text("ConfigFiles")}"]));
        }

        return [.. projects.Where(p => p.Id.Length > 0).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static void Require(DockerItem item)
    {
        if (!Id().IsMatch(item.Id))
        {
            throw new ArgumentException($"'{item.Id}' is not a container, image or volume id.");
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.:@/-]{0,254}$")]
    private static partial Regex Id();
}
