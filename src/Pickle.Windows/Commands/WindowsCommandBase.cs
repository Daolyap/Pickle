using System.Globalization;
using Pickle.Abstractions;

namespace Pickle.Windows.Commands;

/// <summary>Shared plumbing for the Windows <c>pk</c> commands: error mapping, confirmation, styled output.</summary>
internal abstract class WindowsCommandBase : IPickleCommand
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
internal sealed class CommandOutput(PickleCommandContext context)
{
    public PickleCommandContext Context { get; } = context;

    public IPickleContext Pickle => Context.Pickle;

    private UiColors Ui => Context.Pickle.Themes.Current.Ui;

    public void Object(object? value) => Context.WriteObject(value);

    public void Line(string text = "") => Context.WriteHost(text);

    public void Heading(string text) => Context.WriteHost(Ansi.Colorize(text, Ui.Accent, bold: true));

    public void Muted(string text) => Context.WriteHost(Ansi.Colorize(text, Ui.Muted));

    public void Success(string text) => Context.WriteHost(Ansi.Colorize("✓ ", Ui.Success) + text);

    public void Failure(string text) => Context.WriteHost(Ansi.Colorize("✗ ", Ui.Error) + text);

    public void Warning(string text) => Context.WriteHost(Ansi.Colorize("! ", Ui.Warning) + text);

    public string Accent(string text) => Ansi.Colorize(text, Ui.Accent);

    public string Dim(string text) => Ansi.Colorize(text, Ui.Muted);

    public string Warn(string text) => Ansi.Colorize(text, Ui.Warning);

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
}
