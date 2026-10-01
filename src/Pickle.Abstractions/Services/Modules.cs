namespace Pickle.Abstractions.Services;

[Flags]
public enum ModulePlatforms
{
    None = 0,
    Windows = 1,
    Linux = 2,
    MacOS = 4,
    All = Windows | Linux | MacOS,
}

/// <summary>
/// An optional feature that is compiled into Pickle but only loaded when selected: at install time (MSI feature,
/// RPM sub-package, <c>modules.d</c> marker file), by <c>pk setup</c> or with <c>pk module enable</c>.
/// <see cref="Create"/> runs only for enabled modules, so a disabled one costs nothing at startup.
/// </summary>
public sealed class ModuleDescriptor
{
    /// <summary>Short, lowercase, stable (<c>docker</c>); what <c>pk module enable</c> and the installers use.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public required Func<IPicklePlugin> Create { get; init; }

    public ModulePlatforms Platforms { get; init; } = ModulePlatforms.All;

    /// <summary>External programs the module drives (<c>docker</c>, <c>kubectl</c>); shown by <c>pk module info</c>.</summary>
    public IReadOnlyList<string> Tools { get; init; } = [];

    /// <summary>The <c>pk</c> commands and panels it adds, for <c>pk module info</c> before it is loaded.</summary>
    public IReadOnlyList<string> Provides { get; init; } = [];

    /// <summary>Not listed unless asked for (<c>pk module list --all</c>); the example module is hidden.</summary>
    public bool Hidden { get; init; }

    public bool IsSupportedHere =>
        (OperatingSystem.IsWindows() && Platforms.HasFlag(ModulePlatforms.Windows))
        || (OperatingSystem.IsLinux() && Platforms.HasFlag(ModulePlatforms.Linux))
        || (OperatingSystem.IsMacOS() && Platforms.HasFlag(ModulePlatforms.MacOS));
}

public enum ModuleState
{
    /// <summary>Not selected.</summary>
    Off,

    /// <summary>Selected and loaded.</summary>
    Loaded,

    /// <summary>Selected but not loaded yet (turned on this session, or it failed).</summary>
    Pending,

    /// <summary>Selected, but not for this operating system.</summary>
    Unsupported,

    Failed,
}

public sealed record ModuleStatus(ModuleDescriptor Module, bool Enabled, ModuleState State, string Source, string? Error = null);

/// <summary>The optional modules and which of them are on. Implemented by Pickle.Core; available as a service.</summary>
public interface IModuleCatalog
{
    IReadOnlyList<ModuleDescriptor> Available { get; }

    IReadOnlyList<ModuleStatus> Status();

    ModuleStatus? Find(string id);

    /// <summary>Turns a module on for this user and loads it now. Returns the new status.</summary>
    ModuleStatus Enable(string id);

    /// <summary>Turns a module off for this user. It stays loaded until Pickle restarts (nothing can be unregistered live).</summary>
    ModuleStatus Disable(string id);

    /// <summary>Replaces the user's selection with exactly <paramref name="ids"/> (the picker in <c>pk setup</c>).</summary>
    void Select(IReadOnlyCollection<string> ids);
}
