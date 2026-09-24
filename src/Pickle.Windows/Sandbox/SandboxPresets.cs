using Pickle.Abstractions.Services;

namespace Pickle.Windows.Sandbox;

/// <summary>Built-in sandbox starting points. Each call returns fresh copies (they are edited in the panel).</summary>
public static class SandboxPresets
{
    public static IReadOnlyList<SandboxConfig> Create(string? downloadsFolder, string? currentFolder) =>
    [
        new()
        {
            Name = "Safe browsing",
            Description = "Throwaway browser: networking on, nothing shared, clipboard off, opens Edge.",
            Networking = SandboxSwitch.Enable,
            VGpu = SandboxSwitch.Enable,
            ClipboardRedirection = SandboxSwitch.Disable,
            PrinterRedirection = SandboxSwitch.Disable,
            DarkMode = true,
            StartUrl = "https://www.bing.com",
        },
        new()
        {
            Name = "Test an installer",
            Description = "Try untrusted downloads offline: Downloads shared read-only, extra isolation, file extensions shown.",
            Networking = SandboxSwitch.Disable,
            VGpu = SandboxSwitch.Disable,
            ProtectedClient = SandboxSwitch.Enable,
            ClipboardRedirection = SandboxSwitch.Disable,
            PrinterRedirection = SandboxSwitch.Disable,
            AudioInput = SandboxSwitch.Disable,
            VideoInput = SandboxSwitch.Disable,
            MappedFolders = downloadsFolder is null ? [] : [new SandboxMappedFolder { HostFolder = downloadsFolder, ReadOnly = true }],
            ShowFileExtensions = true,
            ShowHiddenFiles = true,
            OpenMappedFolder = true,
        },
        new()
        {
            Name = "Try apps with winget",
            Description = "Networking on and winget installed at logon; add package ids to install them.",
            Networking = SandboxSwitch.Enable,
            VGpu = SandboxSwitch.Enable,
            InstallWinget = true,
            DarkMode = true,
            ShowFileExtensions = true,
            MemoryInMB = 8192,
        },
        new()
        {
            Name = "Offline analysis",
            Description = "No network, no GPU, no mic/camera, no clipboard or printers: the most locked-down sandbox.",
            Networking = SandboxSwitch.Disable,
            VGpu = SandboxSwitch.Disable,
            ProtectedClient = SandboxSwitch.Enable,
            ClipboardRedirection = SandboxSwitch.Disable,
            PrinterRedirection = SandboxSwitch.Disable,
            AudioInput = SandboxSwitch.Disable,
            VideoInput = SandboxSwitch.Disable,
            ShowFileExtensions = true,
            ShowHiddenFiles = true,
        },
        new()
        {
            Name = "Pickle inside",
            Description = "This Pickle shared read-only and started at logon, with networking: test scripts safely.",
            Networking = SandboxSwitch.Enable,
            IncludePickle = true,
            StartPickle = true,
            DarkMode = true,
            ShowFileExtensions = true,
        },
        new()
        {
            Name = "Dev scratch",
            Description = "The current folder shared read-write, winget with Git and VS Code, 8 GB of memory.",
            Networking = SandboxSwitch.Enable,
            VGpu = SandboxSwitch.Enable,
            MemoryInMB = 8192,
            MappedFolders = currentFolder is null ? [] : [new SandboxMappedFolder { HostFolder = currentFolder, ReadOnly = false }],
            WingetPackages = ["Git.Git", "Microsoft.VisualStudioCode"],
            IncludePickle = true,
            DarkMode = true,
            ShowFileExtensions = true,
            OpenMappedFolder = true,
        },
    ];
}
