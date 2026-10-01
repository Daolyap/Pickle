using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Tui;
using Terminal.Gui.Input;

namespace Pickle.Modules.Docker;

/// <summary>Docker or Podman (Alt+Shift+D): containers, images, volumes and compose projects; F6 switches the view.</summary>
internal sealed class DockerPanel : ResourcePanel<DockerItem>
{
    public const string PanelId = "docker";

    private readonly DockerService _docker;
    private DockerKind _kind = DockerKind.Containers;

    public static PanelDescriptor Descriptor { get; } = new()
    {
        Id = PanelId,
        Title = "Docker",
        Description = "Containers, images, volumes and compose projects (Docker or Podman)",
        DefaultKey = "Alt+Shift+D",
        CreateView = context => new DockerPanel(context),
    };

    public DockerPanel(PanelContext context)
        : base(context, "Docker", i => i.Name, "Details")
    {
        _docker = new DockerService(context.Pickle.Services.Require<IProgramRunner>(), context.Pickle.Config);
        if (Enum.TryParse<DockerKind>(context.Argument, ignoreCase: true, out var kind))
        {
            _kind = kind;
        }

        Retitle();
        AddAction(Key.F2, "Start", (i, ct) => Act(i, "start", ct), enabled: i => i.Kind is DockerKind.Containers or DockerKind.Compose && !i.IsRunning);
        AddAction(Key.F3, "Stop", (i, ct) => Act(i, "stop", ct), confirm: i => $"Stop {i.Name}?", enabled: i => i.Kind is DockerKind.Containers or DockerKind.Compose && i.IsRunning);
        AddAction(Key.F4, "Restart", (i, ct) => Act(i, "restart", ct), enabled: i => i.Kind is DockerKind.Containers or DockerKind.Compose);
        AddAction(Key.F7, "Remove", (i, ct) => Act(i, i.Kind == DockerKind.Compose ? "down" : "remove", ct), confirm: i => $"Remove {i.Name}?");
        AddCommand(Key.F8, "Logs", i => _docker.ShellCommand(i, "logs"));
        AddCommand(Key.F9, "Shell", i => i.Kind == DockerKind.Images ? _docker.ShellCommand(i, "run") : _docker.ShellCommand(i, "shell"));
        AddHint(Key.F6, "Containers/Images/Volumes/Compose", CycleKind);
    }

    protected override TimeSpan? AutoRefresh => TimeSpan.FromSeconds(10);

    protected override string EmptyMessage => _docker.Runtime is null ? "Docker or Podman is not installed (or not on PATH)." : $"No {_kind.ToString().ToLowerInvariant()}.";

    protected override Task<IReadOnlyList<DockerItem>> LoadAsync(CancellationToken cancellationToken) => _docker.ListAsync(_kind, cancellationToken);

    protected override Task<IReadOnlyList<string>> DescribeAsync(DockerItem item, CancellationToken cancellationToken) => _docker.DescribeAsync(item, cancellationToken);

    protected override string KeyOf(DockerItem item) => item.Kind + "|" + item.Id;

    protected override string? Hint(DockerItem item) => item.Kind == DockerKind.Containers ? item.State : null;

    protected override string? Detail(DockerItem item) => item.Summary;

    protected override Terminal.Gui.Drawing.Color? ItemColor(DockerItem item) =>
        item.State is "exited" or "dead" ? Schemes.Muted.Foreground : item.IsRunning ? Schemes.Success.Foreground : null;

    private async Task<ActionOutcome> Act(DockerItem item, string verb, CancellationToken cancellationToken)
    {
        var result = await _docker.ActAsync(item, verb, cancellationToken).ConfigureAwait(false);
        return result.Success ? ActionOutcome.Done(result.Message) : ActionOutcome.Fail(result.Message);
    }

    private void CycleKind()
    {
        _kind = (DockerKind)(((int)_kind + 1) % Enum.GetValues<DockerKind>().Length);
        Retitle();
        Reload();
    }

    private void Retitle() => PanelTitle = $"Docker ({_docker.Runtime ?? "not installed"}) · {_kind.ToString().ToLowerInvariant()}";
}
