using System.Management.Automation.Runspaces;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Core.Contracts;
using Pickle.Core.Prompt.Segments;

namespace Pickle.Core.Prompt;

/// <summary>
/// Renders the theme's prompt segments (left, right, separators, transient prompt), keeps slow segments off the
/// typing path via <see cref="SegmentCache"/>, and mirrors the theme into $PSStyle.
/// </summary>
public sealed class PromptEngine : IPromptRenderer, IRuntimeComponent, IDisposable
{
    private readonly PickleRuntime _runtime;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SegmentCache _cache;
    private readonly PromptComposer _composer;
    private IDisposable? _postExecuteHook;
    private bool _themeSubscribed;
    private bool _renderedOnce;
    private int _psStyleDirty = 1;

    public PromptEngine(PickleRuntime runtime)
    {
        _runtime = runtime;
        _cache = new SegmentCache(runtime.Log);
        _cache.LateResultAvailable += (_, _) => SegmentsRefreshed?.Invoke(this, EventArgs.Empty);
        _composer = new PromptComposer(type => _runtime.PromptSegmentRegistry.Get(type), _cache, _lifetime.Token);
    }

    /// <summary>
    /// Raised on a background thread when a slow segment finished after the prompt was already drawn without it
    /// (or with a stale value). A line editor may redraw the prompt in response.
    /// </summary>
    public event EventHandler? SegmentsRefreshed;

    public void Initialize()
    {
        var environment = SegmentEnvironment.Create(
            () => _runtime.ServiceRegistry.Get<IGitService>(),
            () => _runtime.Config.Current.Prompt.DurationThresholdMs);
        foreach (var segment in BuiltInSegments.Create(environment))
        {
            _runtime.PromptSegmentRegistry.Register(segment);
        }

        _runtime.CommandRegistry.Register(new ThemeCommand(this));
        _postExecuteHook ??= _runtime.Hooks.Register(HookKind.PostExecute, (_, _) =>
        {
            InvalidateSegments();
            return ValueTask.CompletedTask;
        });

        if (!_themeSubscribed)
        {
            _runtime.Themes.ThemeChanged += OnThemeChanged;
            _themeSubscribed = true;
        }
    }

    public void OnStarted()
    {
        Volatile.Write(ref _psStyleDirty, 1);
        SyncPsStyle();
    }

    public PromptRender Render(PromptContext context)
    {
        SyncPsStyle();
        var render = Render(context, _runtime.Themes.Current);
        if (_runtime.Config.Current.Prompt.NewlineBeforePrompt && _renderedOnce)
        {
            render = render with { Left = "\n" + render.Left };
        }

        _renderedOnce = true;
        return render;
    }

    /// <summary>Renders any theme against the live segments and cache (no newline-before-prompt handling).</summary>
    public PromptRender Render(PromptContext context, Theme theme) =>
        _composer.Compose(context, theme, _runtime.Config.Current.Prompt.GitTimeoutMs);

    public string RenderTransient(PromptContext context) => _composer.ComposeTransient(context, _runtime.Themes.Current);

    public void Prefetch(PromptContext context) => _composer.Prefetch(context, _runtime.Themes.Current);

    /// <summary>Marks cached segment values stale (called after every command) so the next render refreshes them.</summary>
    public void InvalidateSegments() => _cache.Invalidate();

    /// <summary>Sample prompt for a theme (fixed data, independent of the current directory).</summary>
    public PromptRender RenderPreview(Theme theme, int width, bool lastCommandSucceeded = false) =>
        new ThemePreview(OperatingSystem.IsWindows()).Render(theme, width, lastCommandSucceeded);

    public int TerminalWidth => _runtime.Terminal.Width;

    public void Dispose()
    {
        _lifetime.Cancel();
        _postExecuteHook?.Dispose();
        if (_themeSubscribed)
        {
            _runtime.Themes.ThemeChanged -= OnThemeChanged;
            _themeSubscribed = false;
        }

        _lifetime.Dispose();
    }

    private void OnThemeChanged(object? sender, Theme theme)
    {
        Volatile.Write(ref _psStyleDirty, 1);
        SyncPsStyle();
    }

    private void SyncPsStyle()
    {
        // A theme switched from inside a pipeline (Set-PickleTheme, `pk theme set`) is applied at the next prompt:
        // the runspace is busy, and waiting for it from the pipeline's own call chain would deadlock.
        var engine = _runtime.Engine;
        if (!engine.IsOpen || engine.MainRunspace.RunspaceAvailability != RunspaceAvailability.Available
            || Interlocked.Exchange(ref _psStyleDirty, 0) == 0)
        {
            return;
        }

        try
        {
            engine.InvokeSilently(PsStyleSync.Script, PsStyleSync.Parameters(_runtime.Themes.Current));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _runtime.Log.Warn("prompt", "Failed to apply the theme to $PSStyle", ex);
        }
    }
}
