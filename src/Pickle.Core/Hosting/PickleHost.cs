using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Management.Automation.Runspaces;

namespace Pickle.Core.Hosting;

/// <summary>
/// Pickle's PSHost. Owning the host (instead of wrapping pwsh.exe) is what lets Pickle replace line editing,
/// rendering and prompts while running the real PowerShell engine.
/// </summary>
public sealed class PickleHost : PSHost, IHostSupportsInteractiveSession
{
    private readonly PickleRuntime _runtime;
    private readonly PickleHostUserInterface _ui;
    private readonly Stack<Runspace> _pushedRunspaces = new();

    public PickleHost(PickleRuntime runtime)
    {
        _runtime = runtime;
        _ui = new PickleHostUserInterface(runtime);
    }

    public override string Name => _runtime.Config.Current.Shell.HostCompatibility ? "ConsoleHost" : "Pickle";

    public override Version Version { get; } = typeof(PickleHost).Assembly.GetName().Version ?? new Version(0, 1);

    public override Guid InstanceId { get; } = Guid.NewGuid();

    public override PSHostUserInterface UI => _ui;

    public override CultureInfo CurrentCulture => CultureInfo.CurrentCulture;

    public override CultureInfo CurrentUICulture => CultureInfo.CurrentUICulture;

    public override PSObject PrivateData { get; } = new(new HostPrivateData());

    public PickleHostUserInterface PickleUI => _ui;

    public override void SetShouldExit(int exitCode) => _runtime.RequestExit(exitCode);

    public override void EnterNestedPrompt() => _runtime.Repl.RunNestedPrompt();

    public override void ExitNestedPrompt() => _runtime.Repl.ExitNestedPrompt();

    public override void NotifyBeginApplication()
    {
    }

    public override void NotifyEndApplication()
    {
    }

    // IHostSupportsInteractiveSession: enables Enter-PSSession / Exit-PSSession.
    public bool IsRunspacePushed => _pushedRunspaces.Count > 0;

    public Runspace Runspace => _pushedRunspaces.Count > 0 ? _pushedRunspaces.Peek() : _runtime.Engine.MainRunspace;

    public void PushRunspace(Runspace runspace) => _pushedRunspaces.Push(runspace);

    public void PopRunspace()
    {
        if (_pushedRunspaces.Count > 0)
        {
            _pushedRunspaces.Pop();
        }
    }

    /// <summary>Mirrors ConsoleHost's $Host.PrivateData color properties so scripts that set them don't fail.</summary>
    public sealed class HostPrivateData
    {
        public ConsoleColor ErrorForegroundColor { get; set; } = ConsoleColor.Red;
        public ConsoleColor ErrorBackgroundColor { get; set; } = ConsoleColor.Black;
        public ConsoleColor WarningForegroundColor { get; set; } = ConsoleColor.Yellow;
        public ConsoleColor WarningBackgroundColor { get; set; } = ConsoleColor.Black;
        public ConsoleColor DebugForegroundColor { get; set; } = ConsoleColor.Yellow;
        public ConsoleColor DebugBackgroundColor { get; set; } = ConsoleColor.Black;
        public ConsoleColor VerboseForegroundColor { get; set; } = ConsoleColor.Yellow;
        public ConsoleColor VerboseBackgroundColor { get; set; } = ConsoleColor.Black;
        public ConsoleColor ProgressForegroundColor { get; set; } = ConsoleColor.Yellow;
        public ConsoleColor ProgressBackgroundColor { get; set; } = ConsoleColor.DarkCyan;
    }
}
