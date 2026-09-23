namespace Pickle.Abstractions;

/// <summary>Context handed to a panel factory.</summary>
public sealed class PanelContext
{
    public required IPickleContext Pickle { get; init; }

    /// <summary>Optional argument, e.g. a path for the file picker or a wizard id.</summary>
    public string? Argument { get; init; }

    /// <summary>Current input line when the panel was opened from the editor (wizards use this for parse-back).</summary>
    public string? CurrentInput { get; init; }

    /// <summary>Set by the panel before it closes to tell the shell what to do next.</summary>
    public PanelResult? Result { get; set; }
}

public enum PanelResultKind
{
    None,

    /// <summary>Insert <see cref="PanelResult.Text"/> at the cursor.</summary>
    InsertText,

    /// <summary>Replace the input line with <see cref="PanelResult.Text"/>.</summary>
    ReplaceInput,

    /// <summary>Run <see cref="PanelResult.Text"/> as a command.</summary>
    RunCommand,

    /// <summary>Change directory to <see cref="PanelResult.Text"/>.</summary>
    ChangeDirectory,
}

public sealed record PanelResult(PanelResultKind Kind, string Text = "");

/// <summary>
/// Describes a full-screen panel. <see cref="CreateView"/> must return a <c>Terminal.Gui.Views.Runnable</c>
/// (typed as object so this assembly stays UI-free). Panels close themselves via Terminal.Gui RequestStop.
/// </summary>
public sealed class PanelDescriptor
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }

    /// <summary>Default chord, e.g. "Alt+G". The key binding action is named "panel.{Id}".</summary>
    public string? DefaultKey { get; init; }

    public bool WindowsOnly { get; init; }

    public required Func<PanelContext, object> CreateView { get; init; }
}

/// <summary>Declarative list panel for PowerShell plugins (<c>Register-PicklePanel</c>).</summary>
public sealed class ListPanelSpec
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string Description { get; init; } = string.Empty;
    public string? DefaultKey { get; init; }

    /// <summary>Script returning the items; each item's ToString() (or its "Name" property) is displayed.</summary>
    public required string ItemsScript { get; init; }

    /// <summary>Action label → script run with <c>$_</c> bound to the selected item.</summary>
    public Dictionary<string, string> Actions { get; init; } = [];
}

public interface IPanelRegistry
{
    void Register(PanelDescriptor panel);

    void RegisterList(ListPanelSpec spec);

    PanelDescriptor? Get(string id);

    IReadOnlyList<PanelDescriptor> All { get; }

    IReadOnlyList<ListPanelSpec> ListPanels { get; }
}

/// <summary>Shows panels. Implemented by Pickle.Tui; available via <see cref="IPickleServices"/>.</summary>
public interface IPanelHost
{
    /// <summary>Runs a panel modally (blocking the REPL thread) and returns its result.</summary>
    PanelResult? Show(string panelId, string? argument = null, string? currentInput = null);

    /// <summary>Runs a panel produced by an ad-hoc factory (used by the command palette and wizards).</summary>
    PanelResult? Show(PanelDescriptor panel, string? argument = null, string? currentInput = null);
}
