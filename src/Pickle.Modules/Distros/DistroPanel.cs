using Pickle.Abstractions;
using Pickle.Tui;
using Terminal.Gui.Input;

namespace Pickle.Modules.Distros;

/// <summary>WSL distributions and Linux containers (Alt+Shift+W): start, stop, set the default, export, remove, open a shell in one.</summary>
internal sealed class DistroPanel : ResourcePanel<Distro>
{
    public const string PanelId = "distros";

    private readonly DistroService _service;
    private string _problems = string.Empty;

    public static PanelDescriptor Descriptor { get; } = new()
    {
        Id = PanelId,
        Title = "WSL & containers",
        Description = "WSL distributions, Distrobox, Toolbx, LXC and Incus containers",
        DefaultKey = "Alt+Shift+W",
        CreateView = context => new DistroPanel(context),
    };

    public DistroPanel(PanelContext context)
        : base(context, "WSL & containers", d => d.Name, "Details")
    {
        _service = context.Pickle.Services.Require<DistroService>();
        PanelTitle = "WSL & containers · " + (_service.Available.Count == 0 ? "nothing installed" : string.Join(", ", _service.Available.Select(b => b.DisplayName)));
        AddAction(Key.F2, "Start", (d, ct) => Act(d, "start", ct), enabled: d => !d.IsRunning && _service.Supports(d, "start"));
        AddAction(Key.F3, "Stop", (d, ct) => Act(d, "stop", ct), confirm: d => $"Stop {d.Name}?", enabled: d => d.IsRunning && _service.Supports(d, "stop"));
        AddAction(Key.F4, "Restart", (d, ct) => Act(d, "restart", ct), enabled: d => _service.Supports(d, "restart"));
        AddAction(Key.F6, "Default", (d, ct) => Act(d, "default", ct), enabled: d => !d.IsDefault && _service.Supports(d, "default"));
        AddAction(
            Key.F7,
            "Remove",
            (d, ct) => Act(d, "remove", ct),
            confirm: d => $"Remove {d.Name}?\n\nThis deletes it and everything stored inside it. It cannot be undone.");
        AddCommand(Key.F8, "Export", d => _service.BackendOf(d).ShellLine(d, "export"));
        AddCommand(Key.F9, "Enter", d => _service.BackendOf(d).ShellLine(d, "enter"));
        AddHint(Key.N.WithCtrl, "New", CreateNew);
    }

    protected override TimeSpan? AutoRefresh => TimeSpan.FromSeconds(15);

    protected override string EmptyMessage => _problems.Length > 0
        ? _problems
        : _service.Available.Count == 0
            ? "None of wsl.exe, distrobox, toolbox, lxc or incus was found on PATH."
            : "No distributions or containers yet. Ctrl+N creates one.";

    protected override async Task<IReadOnlyList<Distro>> LoadAsync(CancellationToken cancellationToken)
    {
        var listing = await _service.ListAsync(cancellationToken).ConfigureAwait(false);
        _problems = string.Join("\n\n", listing.Errors);
        return listing.Items;
    }

    protected override Task<IReadOnlyList<string>> DescribeAsync(Distro item, CancellationToken cancellationToken) => _service.DescribeAsync(item, cancellationToken);

    protected override string KeyOf(Distro item) => item.Backend + "|" + item.Name;

    protected override string? Category(Distro item) => _service.BackendOf(item).DisplayName;

    protected override string? Hint(Distro item) => item.State;

    protected override string? Detail(Distro item) => item.Summary;

    protected override Terminal.Gui.Drawing.Color? ItemColor(Distro item) =>
        item.IsRunning ? Schemes.Success.Foreground : item.State == "stopped" ? Schemes.Muted.Foreground : null;

    private async Task<ActionOutcome> Act(Distro distro, string verb, CancellationToken cancellationToken)
    {
        var result = await _service.ActAsync(distro, verb, cancellationToken).ConfigureAwait(false);
        return result.Success ? ActionOutcome.Done(result.Message) : ActionOutcome.Fail(result.Message);
    }

    private void CreateNew()
    {
        var available = _service.Available;
        if (available.Count == 0)
        {
            ShowError("None of wsl.exe, distrobox, toolbox, lxc or incus was found on PATH.");
            return;
        }

        var backend = available.Count == 1 ? available[0] : Pick("Create in…", available, b => b.DisplayName);
        if (backend is null)
        {
            return;
        }

        var hint = backend.Id == "wsl" ? "distribution to install (see: pk wsl online)" : "name, then the image: dev ubuntu:24.04";
        if (Prompt("New", hint) is not { Length: > 0 } answer)
        {
            return;
        }

        var words = answer.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        try
        {
            Complete(new PanelResult(PanelResultKind.RunCommand, backend.CreateLine(words[0], words.Length > 1 ? words[1] : null)));
        }
        catch (ArgumentException ex)
        {
            ShowError(ex.Message);
        }
    }
}
