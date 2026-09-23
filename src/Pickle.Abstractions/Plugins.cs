namespace Pickle.Abstractions;

/// <summary>
/// A Pickle plugin. Built-in features (panels, Windows integrations, wizards) are plugins too and are
/// registered by the composition root; third-party .NET plugins are loaded from the plugins folder.
/// </summary>
public interface IPicklePlugin
{
    /// <summary>Stable, lowercase, dotted id, e.g. <c>pickle.git</c>.</summary>
    string Id { get; }

    string DisplayName { get; }

    string Description { get; }

    /// <summary>Called once at startup, after core services exist and before the first prompt.</summary>
    void Initialize(IPickleContext context);
}

/// <summary>Everything a plugin can reach. Implemented by <c>Pickle.Core.PickleRuntime</c>.</summary>
public interface IPickleContext
{
    PicklePaths Paths { get; }
    IPickleShell Shell { get; }
    IConfigStore Config { get; }
    IThemeProvider Themes { get; }
    IAliasRegistry Aliases { get; }
    IHistoryStore History { get; }
    IPromptSegmentRegistry PromptSegments { get; }
    ICompletionRegistry Completions { get; }
    IKeyBindingRegistry KeyBindings { get; }
    ICommandRegistry Commands { get; }
    IPanelRegistry Panels { get; }
    IWizardRegistry Wizards { get; }
    ITranslationRegistry Translations { get; }
    IHookRegistry Hooks { get; }
    IPickleServices Services { get; }
    IPickleLogger Log { get; }
}

/// <summary>Tiny service locator for optional cross-cutting services (IWingetService, IGitService, IPanelHost, ...).</summary>
public interface IPickleServices
{
    void Add<T>(T implementation) where T : class;
    T? Get<T>() where T : class;
    T Require<T>() where T : class;
}

public enum PickleLogLevel
{
    Trace,
    Debug,
    Info,
    Warning,
    Error,
}

public interface IPickleLogger
{
    void Log(PickleLogLevel level, string category, string message, Exception? exception = null);
}

public static class PickleLoggerExtensions
{
    public static void Debug(this IPickleLogger log, string category, string message) => log.Log(PickleLogLevel.Debug, category, message);
    public static void Info(this IPickleLogger log, string category, string message) => log.Log(PickleLogLevel.Info, category, message);
    public static void Warn(this IPickleLogger log, string category, string message, Exception? ex = null) => log.Log(PickleLogLevel.Warning, category, message, ex);
    public static void Error(this IPickleLogger log, string category, string message, Exception? ex = null) => log.Log(PickleLogLevel.Error, category, message, ex);
}
