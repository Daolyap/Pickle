using System.Globalization;

namespace Pickle.Abstractions;

/// <summary>
/// Base for <c>pk</c> commands (built-in and plugins): argument/IO errors become exit codes and error lines, output goes
/// through a theme-colored <see cref="CommandOutput"/>.
/// </summary>
public abstract class PickleCommandBase : IPickleCommand
{
    public abstract string Name { get; }

    public abstract string Description { get; }

    public abstract string Usage { get; }

    public async ValueTask<int> ExecuteAsync(PickleCommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        try
        {
            return await RunAsync(new CommandOutput(context), args, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            context.WriteError(ex.Message);
            return 2;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or IOException or UnauthorizedAccessException or TimeoutException)
        {
            context.Pickle.Log.Warn("pk", $"pk {Name} failed", ex);
            context.WriteError(ex.Message);
            return 1;
        }
    }

    protected abstract Task<int> RunAsync(CommandOutput output, IReadOnlyList<string> args, CancellationToken cancellationToken);

    protected int UsageError(CommandOutput output, string? message = null)
    {
        if (message is not null)
        {
            output.Context.WriteError(message);
        }

        output.Muted("usage: " + Usage);
        return 2;
    }
}

/// <summary>Theme-colored host output helpers.</summary>
public sealed class CommandOutput(PickleCommandContext context)
{
    private readonly object _gate = new();
    private bool _statusVisible;
    private int? _width;

    public PickleCommandContext Context { get; } = context;

    public IPickleContext Pickle => Context.Pickle;

    private UiColors Ui => Context.Pickle.Themes.Current.Ui;

    public void Object(object? value) => Context.WriteObject(value);

    public void Line(string text = "") => Host(text);

    public void Heading(string text) => Host(Ansi.Colorize(text, Ui.Accent, bold: true));

    public void Muted(string text) => Host(Ansi.Colorize(text, Ui.Muted));

    public void Success(string text) => Host(Ansi.Colorize("✓ ", Ui.Success) + text);

    public void Failure(string text) => Host(Ansi.Colorize("✗ ", Ui.Error) + text);

    public void Warning(string text) => Host(Ansi.Colorize("! ", Ui.Warning) + text);

    /// <summary>Writes (or rewrites in place) the single status line of a running operation; the next line replaces it.</summary>
    public void Status(string text)
    {
        lock (_gate)
        {
            Context.WriteHost(TakeStatusLine() + "\r" + text + Ansi.ClearToEndOfLine);
            _statusVisible = true;
        }
    }

    /// <summary>Multi-line program output (winget, PowerShell), indented and dimmed.</summary>
    public void Transcript(string? text, int maxLines = 60)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length > maxLines)
        {
            Muted($"  … ({lines.Length - maxLines} earlier line(s) omitted)");
            lines = lines[^maxLines..];
        }

        foreach (var line in lines)
        {
            Muted("  │ " + line.TrimEnd());
        }
    }

    public string Accent(string text) => Ansi.Colorize(text, Ui.Accent);

    public string Dim(string text) => Ansi.Colorize(text, Ui.Muted);

    public string Warn(string text) => Ansi.Colorize(text, Ui.Warning);

    /// <summary>The terminal width (from the PowerShell host), for host-side tables; 100 when unknown.</summary>
    public async Task<int> WidthAsync()
    {
        if (_width is { } known)
        {
            return known;
        }

        try
        {
            var result = await Pickle.Shell.InvokeAsync("$Host.UI.RawUI.WindowSize.Width").ConfigureAwait(false);
            _width = result.Output.FirstOrDefault()?.BaseObject is int width && width >= 40 ? width : 100;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Management.Automation.RuntimeException)
        {
            _width = 100;
        }

        return _width.Value;
    }

    /// <summary><c>--yes</c> skips the question; otherwise asks (non-interactive sessions get <paramref name="defaultYes"/>).</summary>
    public bool Confirm(CommandArgs args, string question, bool defaultYes) => args.Yes || Context.Confirm(question, defaultYes);

    public static string Size(long? bytes) => bytes switch
    {
        null => string.Empty,
        >= 1024L * 1024 * 1024 => (bytes.Value / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
        >= 1024L * 1024 => (bytes.Value / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        _ => (bytes.Value / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB",
    };

    public static string When(DateTimeOffset? value) =>
        value is { } v ? v.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "never";

    public static string Elapsed(TimeSpan value) =>
        value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : value.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    // Every host line goes through here: a visible status line is replaced by the next line instead of left behind.
    private void Host(string text)
    {
        lock (_gate)
        {
            Context.WriteHost(TakeStatusLine() + text);
        }
    }

    private string TakeStatusLine()
    {
        if (!_statusVisible)
        {
            return string.Empty;
        }

        _statusVisible = false;
        return Ansi.CursorUp(1) + "\r" + Ansi.ClearToEndOfLine;
    }
}
