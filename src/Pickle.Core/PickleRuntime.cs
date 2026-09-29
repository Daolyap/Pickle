using System.Reflection;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Core.Aliases;
using Pickle.Core.Completion;
using Pickle.Core.Config;
using Pickle.Core.Contracts;
using Pickle.Core.Git;
using Pickle.Core.History;
using Pickle.Core.Hosting;
using Pickle.Core.Input;
using Pickle.Core.Plugins;
using Pickle.Core.Profile;
using Pickle.Core.Prompt;
using Pickle.Core.Registries;
using Pickle.Core.Sync;
using Pickle.Core.Syntax;
using Pickle.Core.Terminal;
using Pickle.Core.Translation;

namespace Pickle.Core;

/// <summary>
/// The composition root for everything inside the shell process. Implements <see cref="IPickleContext"/> for
/// plugins. Components are constructed here with their final class names; each workstream owns its class file
/// and pulls collaborators from this object lazily (never in constructors), so construction order doesn't matter.
/// </summary>
public sealed class PickleRuntime : IPickleContext, IDisposable
{
    public PickleRuntime(PickleOptions options, ITerminal terminal, PicklePaths paths, IPickleLogger log)
    {
        Startup = new StartupTimings();
        Current = this;
        Options = options;
        Terminal = terminal;
        Paths = paths;
        Log = log;

        IsElevated = options.Elevated ?? Environment.IsPrivilegedProcess;
        ConfigStore = new JsonConfigStore(paths, log);
        ThemeProvider = new ThemeProvider(paths, ConfigStore, log, IsElevated);
        HookRegistry = new HookRegistry(log);
        Engine = new ShellEngine(this);
        Repl = new Repl(this);
        TabProgress = new TabProgress(this);

        History = new JsonlHistoryStore(this);
        var aliases = new AliasManager(this);
        Aliases = aliases;
        AliasMaterializer = aliases;
        Highlighter = new SyntaxHighlighter(this);
        Autosuggest = new HistoryAutosuggest(this);
        Completion = new CompletionEngine(this);
        Prompt = new PromptEngine(this);
        Translation = new TranslationPipeline(this);
        Plugins = new PluginManager(this);
        Sync = new SyncService(this);
        LineEditor = new LineEditor(this);
        ProfileLoader = new ProfileLoader(this);

        ByHost[Engine.Host.InstanceId] = this;
        ServiceRegistry.Add<IPickleShell>(Engine);
        ServiceRegistry.Add<IGitService>(new GitService(log));
        ServiceRegistry.Add<IFirstRunOffers>(FirstRun);
        ServiceRegistry.Add(this);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, PickleRuntime> ByHost = new();

    /// <summary>The most recently created runtime. Prefer <see cref="Resolve"/> from cmdlets (tests run several runtimes).</summary>
    public static PickleRuntime? Current { get; private set; }

    /// <summary>Find the runtime that owns the host a cmdlet is running under (falls back to <see cref="Current"/>).</summary>
    public static PickleRuntime Resolve(System.Management.Automation.Host.PSHost? host) =>
        (host is not null && ByHost.TryGetValue(host.InstanceId, out var runtime) ? runtime : Current)
        ?? throw new InvalidOperationException("Pickle runtime is not running.");

    public static string Version { get; } =
        typeof(PickleRuntime).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    public static string PowerShellVersion { get; } =
        typeof(System.Management.Automation.PSObject).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+', ' ')[0]
        ?? "7";

    public PickleOptions Options { get; }

    /// <summary>Running as administrator (root elsewhere): the admin theme, logo and title apply.</summary>
    public bool IsElevated { get; }

    public ITerminal Terminal { get; }
    public PicklePaths Paths { get; }
    public IPickleLogger Log { get; }

    // Stores and registries
    public JsonConfigStore ConfigStore { get; }
    public ThemeProvider ThemeProvider { get; }
    public PromptSegmentRegistry PromptSegmentRegistry { get; } = new();
    public CompletionRegistry CompletionRegistry { get; } = new();
    public KeyBindingRegistry KeyBindingRegistry { get; } = new();
    public CommandRegistry CommandRegistry { get; } = new();
    public PanelRegistry PanelRegistry { get; } = new();
    public WizardRegistry WizardRegistry { get; } = new();
    public TranslationRegistry TranslationRegistry { get; } = new();
    public HookRegistry HookRegistry { get; }
    public PickleServices ServiceRegistry { get; } = new();
    public FirstRun FirstRun { get; } = new();

    /// <summary>How long each startup phase took (<c>pk doctor --startup</c>).</summary>
    public StartupTimings Startup { get; }

    /// <summary>Terminal tab/taskbar progress for interactive commands.</summary>
    public TabProgress TabProgress { get; }

    // Components
    public ShellEngine Engine { get; }
    public Repl Repl { get; }
    public IHistoryStore History { get; }
    public IAliasRegistry Aliases { get; }
    public IAliasMaterializer AliasMaterializer { get; }
    public ISyntaxHighlighter Highlighter { get; }
    public IAutosuggestProvider Autosuggest { get; }
    public ICompletionEngine Completion { get; }
    public IPromptRenderer Prompt { get; }
    public ITranslationPipeline Translation { get; }
    public IPluginManager Plugins { get; }
    public ISyncService Sync { get; }
    public ILineEditor LineEditor { get; }
    public ProfileLoader ProfileLoader { get; }

    public List<ISessionStateContributor> SessionContributors { get; } = [];

    public bool ExitRequested { get; private set; }
    public int ExitCode { get; private set; }

    // IPickleContext
    public IPickleShell Shell => Engine;
    public IConfigStore Config => ConfigStore;
    public IThemeProvider Themes => ThemeProvider;
    public IPromptSegmentRegistry PromptSegments => PromptSegmentRegistry;
    public ICompletionRegistry Completions => CompletionRegistry;
    public IKeyBindingRegistry KeyBindings => KeyBindingRegistry;
    public ICommandRegistry Commands => CommandRegistry;
    public IPanelRegistry Panels => PanelRegistry;
    public IWizardRegistry Wizards => WizardRegistry;
    public ITranslationRegistry Translations => TranslationRegistry;
    public IHookRegistry Hooks => HookRegistry;
    public IPickleServices Services => ServiceRegistry;

    private IEnumerable<object> Components =>
    [
        History, Aliases, Highlighter, Autosuggest, Completion, Prompt, Translation, Plugins, Sync, LineEditor, ProfileLoader,
    ];

    /// <summary>Phase 1: components register actions/commands/segments/session contributions.</summary>
    public void InitializeComponents()
    {
        Startup.Mark("runtime");
        CommandRegistry.Register(new Commands.VersionCommand());
        CommandRegistry.Register(new Commands.SetupCommand(this));
        foreach (var component in Components.OfType<IRuntimeComponent>())
        {
            component.Initialize();
        }

        Startup.Mark("components");
    }

    /// <summary>Phase 2: open the runspace, load plugins, define aliases/shims, run the profile.</summary>
    public void Start(IReadOnlyList<IPicklePlugin> builtInPlugins)
    {
        Engine.Open();
        Startup.Mark("runspace");
        Plugins.LoadAll(builtInPlugins);
        Startup.Mark("plugins");
        foreach (var component in Components.OfType<IRuntimeComponent>())
        {
            try
            {
                component.OnStarted();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Error("startup", $"{component.GetType().Name}.OnStarted failed", ex);
            }
        }

        Startup.Mark("started");
        if (!Options.NoProfile)
        {
            ProfileLoader.Load();
            Startup.Mark("profile");
        }
    }

    public void RequestExit(int exitCode)
    {
        ExitCode = exitCode;
        ExitRequested = true;
    }

    public PromptContext CreatePromptContext()
    {
        var last = Engine.LastResult;
        return new PromptContext(
            Cwd: Engine.CurrentDirectory,
            LastCommandSucceeded: last?.Success ?? true,
            LastExitCode: last?.ExitCode,
            LastCommandDuration: last?.Duration,
            IsAdmin: IsElevated,
            JobCount: Engine.RunningJobCount,
            UserName: Environment.UserName,
            HostName: Environment.MachineName,
            Now: DateTimeOffset.Now,
            TerminalWidth: Terminal.Width,
            Pickle: this);
    }

    public void Dispose()
    {
        foreach (var component in Components.OfType<IDisposable>())
        {
            component.Dispose();
        }

        Engine.Dispose();
        ByHost.TryRemove(Engine.Host.InstanceId, out _);
        (Log as IDisposable)?.Dispose();
        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }
    }
}
