using System.Text;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Modules.Notifier;

/// <summary>Stored under <c>extensions.notifier</c>: <c>pk config set extensions.notifier.minSeconds 10</c>.</summary>
public sealed class NotifierSettings
{
    /// <summary>Notify when a command ran at least this long.</summary>
    public int MinSeconds { get; set; } = 30;

    /// <summary>Only notify for commands that failed.</summary>
    public bool OnlyFailures { get; set; } = false;

    /// <summary>First words of commands that never notify (interactive programs you sit in front of).</summary>
    public List<string> Ignore { get; set; } = ["vim", "nvim", "vi", "nano", "less", "more", "man", "top", "htop", "btop", "ssh", "pk", "tmux", "screen", "watch", "tail"];
}

/// <summary>Where a notification goes. The OS implementations start fixed programs with the text as a separate argument or environment variable.</summary>
public interface INotificationSink
{
    bool IsAvailable { get; }

    Task<bool> NotifyAsync(string title, string body, CancellationToken cancellationToken);
}

/// <summary>Windows toast (a fixed script in Windows PowerShell 5.1), <c>notify-send</c> on Linux, <c>osascript</c> on macOS; the terminal bell when none is there.</summary>
public sealed class SystemNotificationSink(Func<IProgramRunner> runnerFactory) : INotificationSink
{
    private IProgramRunner runner => runnerFactory();

    internal const string ToastScript = """
        $ErrorActionPreference = 'Stop'
        [void][Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]
        [void][Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime]
        $xml = New-Object Windows.Data.Xml.Dom.XmlDocument
        $xml.LoadXml('<toast><visual><binding template="ToastGeneric"><text id="1"></text><text id="2"></text></binding></visual></toast>')
        $texts = $xml.GetElementsByTagName('text')
        $texts.Item(0).AppendChild($xml.CreateTextNode($env:PICKLE_NOTIFY_TITLE)) | Out-Null
        $texts.Item(1).AppendChild($xml.CreateTextNode($env:PICKLE_NOTIFY_BODY)) | Out-Null
        $appId = '{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe'
        [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($appId).Show([Windows.UI.Notifications.ToastNotification]::new($xml))
        """;

    public bool IsAvailable => OperatingSystem.IsWindows() || runner.Find(OperatingSystem.IsMacOS() ? "osascript" : "notify-send") is not null;

    public async Task<bool> NotifyAsync(string title, string body, CancellationToken cancellationToken)
    {
        ProgramResult result;
        if (OperatingSystem.IsWindows())
        {
            var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            result = await runner.RunAsync(
                powershell,
                ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(ToastScript))],
                new ProgramRunOptions { Environment = new Dictionary<string, string?> { ["PICKLE_NOTIFY_TITLE"] = title, ["PICKLE_NOTIFY_BODY"] = body, ["PSModulePath"] = null }, Timeout = TimeSpan.FromSeconds(10) },
                cancellationToken).ConfigureAwait(false);
        }
        else if (OperatingSystem.IsMacOS())
        {
            result = await runner.RunAsync(
                "osascript",
                ["-e", "display notification (system attribute \"PICKLE_NOTIFY_BODY\") with title (system attribute \"PICKLE_NOTIFY_TITLE\")"],
                new ProgramRunOptions { Environment = new Dictionary<string, string?> { ["PICKLE_NOTIFY_TITLE"] = title, ["PICKLE_NOTIFY_BODY"] = body } },
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            result = await runner.RunAsync("notify-send", ["--app-name=Pickle", "--", title, body], null, cancellationToken).ConfigureAwait(false);
        }

        return result.Success;
    }
}

/// <summary>Sends the notification for a finished command, subject to <see cref="NotifierSettings"/>.</summary>
public sealed class CommandNotifier(IConfigStore config, INotificationSink sink, Action<string> bell)
{
    public async Task OnFinishedAsync(HookEvent e, CancellationToken cancellationToken)
    {
        var settings = config.Get<NotifierSettings>(NotifierModule.ModuleId);
        if (Decide(settings, e) is not { } message)
        {
            return;
        }

        if (!sink.IsAvailable || !await sink.NotifyAsync(message.Title, message.Body, cancellationToken).ConfigureAwait(false))
        {
            bell("\u0007");
        }
    }

    public static (string Title, string Body)? Decide(NotifierSettings settings, HookEvent e)
    {
        if (e.CommandLine is not { Length: > 0 } line || e.Duration is not { } duration || duration < TimeSpan.FromSeconds(Math.Max(1, settings.MinSeconds)))
        {
            return null;
        }

        var failed = e.Success == false;
        if (settings.OnlyFailures && !failed)
        {
            return null;
        }

        var first = line.TrimStart().Split([' ', '\t'], 2)[0];
        if (settings.Ignore.Any(i => string.Equals(i, first, StringComparison.OrdinalIgnoreCase) || string.Equals(i + ".exe", first, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var shown = line.Length <= 80 ? line.Trim() : line.Trim()[..77] + "…";
        return (failed ? "Command failed" : "Command finished", $"{shown} ({Elapsed(duration)})");
    }

    private static string Elapsed(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s" : $"{(int)t.TotalSeconds}s";
}

/// <summary>Desktop notification when a long command finishes (and the terminal bell when there is no notifier).</summary>
public sealed class NotifierModule : IPicklePlugin
{
    public const string ModuleId = "notifier";

    public static ModuleDescriptor Descriptor { get; } = new()
    {
        Id = ModuleId,
        Name = "Long-command notifier",
        Description = "A desktop notification when a command that took a while finishes (toast, notify-send, osascript)",
        Create = () => new NotifierModule(),
        Tools = ["notify-send (Linux)"],
        Provides = ["pk notify", "PostExecute hook"],
    };

    string IPicklePlugin.Id => "pickle." + ModuleId;

    public string DisplayName => Descriptor.Name;

    public string Description => Descriptor.Description;

    public void Initialize(IPickleContext context)
    {
        var sink = new SystemNotificationSink(() => context.Services.Require<IProgramRunner>());
        var notifier = new CommandNotifier(context.Config, sink, text => context.Shell.WriteLine(text));
        context.Hooks.Register(HookKind.PostExecute, async (e, ct) =>
        {
            if (context.Shell.IsInteractive)
            {
                try
                {
                    await notifier.OnFinishedAsync(e, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    context.Log.Debug("notifier", ex.Message);
                }
            }
        });
        context.Commands.Register(new NotifyCommand(sink));
    }
}

internal sealed class NotifyCommand(INotificationSink sink) : PickleCommandBase
{
    public override string Name => "notify";

    public override string Description => "Long-command notifications: test them and see the settings";

    public override string Usage => "pk notify [test | status]";

    protected override async Task<int> RunAsync(CommandOutput output, IReadOnlyList<string> raw, CancellationToken cancellationToken)
    {
        var settings = output.Pickle.Config.Get<NotifierSettings>(NotifierModule.ModuleId);
        switch (raw.Count == 0 ? "status" : raw[0].ToLowerInvariant())
        {
            case "test":
                if (await sink.NotifyAsync("Pickle", "This is a test notification.", cancellationToken).ConfigureAwait(false))
                {
                    output.Success("Sent.");
                    return 0;
                }

                output.Failure(sink.IsAvailable ? "The notification could not be sent." : "No notifier found (Linux needs notify-send, from libnotify); Pickle rings the terminal bell instead.");
                return 1;
            case "status":
                output.Line($"Notifies for commands that run at least {settings.MinSeconds}s{(settings.OnlyFailures ? ", failures only" : string.Empty)}. Ignored: {string.Join(", ", settings.Ignore)}.");
                output.Muted("Change it: pk config set extensions.notifier.minSeconds 10   (also onlyFailures, ignore)");
                return 0;
            default:
                return UsageError(output);
        }
    }
}
