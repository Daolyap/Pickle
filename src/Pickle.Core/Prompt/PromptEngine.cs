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
    private bool _lastRenderHadNewline;
    private int _psStyleDirty = 1;
    private bool? _terminalHasNerdFont;

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
        _runtime.ThemeProvider.RefreshAppearance();
        _lastRenderHadNewline = _runtime.Config.Current.Prompt.NewlineBeforePrompt && _renderedOnce;
        _renderedOnce = true;
        return Rerender(context);
    }

    public PromptRender Rerender(PromptContext context)
    {
        SyncPsStyle();
        var theme = _runtime.Themes.Current;
        var render = Render(context, AnimationFrame is { } frame ? ThemeAnimator.Frame(theme, frame) : theme);
        return _lastRenderHadNewline ? render with { Left = "\n" + render.Left } : render;
    }

    /// <summary>Renders any theme against the live segments and cache (no newline-before-prompt handling).</summary>
    public PromptRender Render(PromptContext context, Theme theme) =>
        _composer.Compose(context, ForTerminal(theme), _runtime.Config.Current.Prompt.GitTimeoutMs);

    /// <summary>Milliseconds for animation timing (replaceable in tests).</summary>
    internal Func<long> Clock { get; set; } = () => Environment.TickCount64;

    public long ClockMs => Clock();

    /// <summary>The current theme's animation frame, or null when it's static or <c>prompt.animation</c> turns animation off.</summary>
    public long? AnimationFrame =>
        _runtime.Themes.Current.Prompt.Animation is { } animation && ThemeAnimator.IsAnimated(_runtime.Themes.Current) && AnimationEnabled
            ? ThemeAnimator.FrameAt(animation, Clock())
            : null;

    /// <summary><c>prompt.animation</c>: "on", "off", or "auto" (off over SSH and on terminals that ask for no color).</summary>
    public bool AnimationEnabled => _runtime.Config.Current.Prompt.Animation?.Trim().ToLowerInvariant() switch
    {
        "on" or "true" or "always" => true,
        "off" or "false" or "never" => false,
        _ => !IsSet("SSH_CONNECTION") && !IsSet("SSH_CLIENT") && !IsSet("SSH_TTY") && !IsSet("NO_COLOR")
            && !string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.Ordinal),
    };

    private static bool IsSet(string variable) => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable));

    public string RenderTransient(PromptContext context) => _composer.ComposeTransient(context, ForTerminal(_runtime.Themes.Current));

    public void Prefetch(PromptContext context) => _composer.Prefetch(context, ForTerminal(_runtime.Themes.Current));

    /// <summary>
    /// Whether themes may use Nerd Font glyphs here: <c>prompt.icons</c> "nerd"/"unicode", or for "auto" what the font
    /// service says about the terminal (checked once per session). Without a font service (not Windows) glyphs stay on.
    /// </summary>
    public bool UseNerdGlyphs => _runtime.Config.Current.Prompt.Icons?.Trim().ToLowerInvariant() switch
    {
        "nerd" => true,
        "unicode" or "plain" or "none" => false,
        _ => _terminalHasNerdFont ??= DetectNerdFont(),
    };

    private Theme ForTerminal(Theme theme) => UseNerdGlyphs ? theme : GlyphFallback.ForUnicode(theme);

    private bool DetectNerdFont()
    {
        try
        {
            return _runtime.ServiceRegistry.Get<IFontService>() is not { } fonts || (fonts.TerminalHasNerdFont() ?? false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _runtime.Log.Warn("prompt", "Could not tell whether the terminal font has Nerd Font glyphs", ex);
            return false;
        }
    }

    /// <summary>Marks cached segment values stale (called after every command) so the next render refreshes them.</summary>
    public void InvalidateSegments() => _cache.Invalidate();

    /// <summary>Sample prompt for a theme (fixed data, independent of the current directory).</summary>
    public PromptRender RenderPreview(Theme theme, int width, bool lastCommandSucceeded = false) =>
        new ThemePreview(OperatingSystem.IsWindows()).Render(ForTerminal(theme), width, lastCommandSucceeded);

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
