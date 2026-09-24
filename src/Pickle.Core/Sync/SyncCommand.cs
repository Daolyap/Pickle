using Pickle.Abstractions;
using Pickle.Core.Commands;
using Pickle.Core.Contracts;

namespace Pickle.Core.Sync;

/// <summary><c>pk sync</c>: sync settings, aliases, themes, wizards, profile and history between machines.</summary>
public sealed class SyncCommand(PickleRuntime runtime, SyncService service) : IPickleCommand
{
    public string Name => "sync";

    public string Description => "Sync settings, aliases, themes, profile and history via a folder or git";

    public string Usage => """
        pk sync [status]                                   Backend, target, last sync and pending changes
        pk sync init <folder|git-url> [--backend folder|git]   Set up sync (e.g. a OneDrive folder or a private repo)
        pk sync push | pull                                Send local changes / bring in remote changes
        pk sync now                                        Push and pull in one go
        pk sync off                                        Stop syncing
        """;

    public async ValueTask<int> ExecuteAsync(PickleCommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var positional = new List<string>();
        string? backend = null;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] is "--backend" or "-b" && i + 1 < args.Count)
            {
                backend = args[++i];
            }
            else if (args[i].StartsWith("--backend=", StringComparison.Ordinal))
            {
                backend = args[i]["--backend=".Length..];
            }
            else
            {
                positional.Add(args[i]);
            }
        }

        var sub = positional.Count > 0 ? positional[0].ToLowerInvariant() : "status";
        SyncReport report;
        switch (sub)
        {
            case "status":
                report = await service.StatusAsync(cancellationToken).ConfigureAwait(false);
                break;
            case "init":
                if (positional.Count < 2)
                {
                    context.WriteError("Usage: pk sync init <folder|git-url> [--backend folder|git]");
                    return 2;
                }

                var target = positional[1];
                if (backend != "git" && !Plugins.PluginCommand.IsGitUrl(target) && !Path.IsPathRooted(target))
                {
                    target = Path.GetFullPath(Path.Combine(context.Cwd, target));
                }

                report = await service.InitAsync(backend ?? "auto", target, cancellationToken).ConfigureAwait(false);
                break;
            case "push":
                report = await service.PushAsync(cancellationToken).ConfigureAwait(false);
                break;
            case "pull":
                report = await service.PullAsync(cancellationToken).ConfigureAwait(false);
                break;
            case "now" or "both":
                report = await service.SyncAsync(cancellationToken).ConfigureAwait(false);
                break;
            case "off":
                report = await service.OffAsync(cancellationToken).ConfigureAwait(false);
                break;
            default:
                var hint = DidYouMean.Suggest(sub, ["status", "init", "push", "pull", "now", "off"]) is [var best, ..] ? $" Did you mean 'pk sync {best}'?" : " Run 'pk sync --help'.";
                context.WriteError($"Unknown subcommand 'pk sync {sub}'.{hint}");
                return 2;
        }

        Write(context, report);
        return report.Success ? 0 : 1;
    }

    private void Write(PickleCommandContext context, SyncReport report)
    {
        var theme = runtime.Themes.Current;
        if (report.Success)
        {
            context.WriteHost(Ansi.Colorize("✔ ", theme.Ui.Success) + report.Message);
        }
        else
        {
            context.WriteError(report.Message);
        }

        foreach (var change in report.Changes)
        {
            var color = change.StartsWith('⚠') ? theme.Ui.Warning : change.StartsWith("plugin ", StringComparison.Ordinal) ? theme.Ui.Info : theme.Ui.Muted;
            context.WriteHost("  " + Ansi.Colorize(change, color));
        }
    }
}
