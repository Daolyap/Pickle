using Pickle.Abstractions;

namespace Pickle.Tui.Panels.Windows;

/// <summary>Registers the winget (Alt+W), Windows Update (Alt+U), Task Scheduler (Alt+S) and Windows Sandbox (Alt+X) panels (Windows only).</summary>
public sealed class WindowsPanelsPlugin : IPicklePlugin
{
    public string Id => "pickle.windows.panels";

    public string DisplayName => "Windows panels";

    public string Description => "winget dashboard, Windows Update and Task Scheduler panels.";

    public void Initialize(IPickleContext context)
    {
        context.Panels.Register(new PanelDescriptor
        {
            Id = "winget",
            Title = "winget",
            Description = "Installed packages, upgrades, search, sources and Windows updates",
            DefaultKey = "Alt+W",
            WindowsOnly = true,
            CreateView = ctx => new WingetPanel(ctx),
        });
        context.Panels.Register(new PanelDescriptor
        {
            Id = "updates",
            Title = "Windows Update",
            Description = "Check for, install and review Windows updates",
            DefaultKey = "Alt+U",
            WindowsOnly = true,
            CreateView = ctx => new UpdatesPanel(ctx),
        });
        context.Panels.Register(new PanelDescriptor
        {
            Id = "scheduler",
            Title = "Task Scheduler",
            Description = "Scheduled tasks: run, enable/disable, delete, history and new tasks",
            DefaultKey = "Alt+S",
            WindowsOnly = true,
            CreateView = ctx => new SchedulerPanel(ctx),
        });
        context.Panels.Register(new PanelDescriptor
        {
            Id = "sandbox",
            Title = "Windows Sandbox",
            Description = "Launch throwaway Windows sandboxes: presets, shared folders, winget packages, Pickle inside",
            DefaultKey = "Alt+X",
            WindowsOnly = true,
            CreateView = ctx => new SandboxPanel(ctx),
        });
    }
}
