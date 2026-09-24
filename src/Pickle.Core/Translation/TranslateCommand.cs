using Pickle.Abstractions;

namespace Pickle.Core.Translation;

/// <summary><c>pk translate on|off|status|list</c>.</summary>
public sealed class TranslateCommand : IPickleCommand
{
    private readonly TranslationPipeline _pipeline;

    public TranslateCommand(TranslationPipeline pipeline) => _pipeline = pipeline;

    public string Name => "translate";

    public string Description => "Linux-syntax translation: rewriters (export, 2>/dev/null, !!, sudo…) and POSIX shims (ls, grep…)";

    public string Usage => "pk translate on [--shims] | off | status | list";

    public ValueTask<int> ExecuteAsync(PickleCommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var verb = args.Count == 0 ? "status" : args[0].ToLowerInvariant();
        switch (verb)
        {
            case "on":
                var shims = args.Skip(1).Any(a => a is "--shims" or "-s");
                context.Pickle.Config.Update(c => c.Translation.Enabled = true);
                _pipeline.RequestReconcile(shims ? true : null);
                context.WriteHost("Translation is on." + (OperatingSystem.IsWindows() || shims
                    ? " POSIX shims (ls, grep, cat…) load for the next command."
                    : " Shims stay off here because native tools exist; use --shims to load them anyway."));
                return ValueTask.FromResult(0);
            case "off":
                context.Pickle.Config.Update(c => c.Translation.Enabled = false);
                _pipeline.RequestReconcile(forceShims: false);
                context.WriteHost("Translation is off: lines run exactly as typed and POSIX shims are unloaded.");
                return ValueTask.FromResult(0);
            case "status":
                var settings = context.Pickle.Config.Current.Translation;
                context.WriteObject(new
                {
                    settings.Enabled,
                    ShimsLoaded = _pipeline.ShimsLoaded,
                    settings.PreferNativeBinaries,
                    Disabled = string.Join(", ", settings.Disabled),
                    Rewriters = string.Join(", ", context.Pickle.Translations.Rewriters.Select(r => r.Name)),
                });
                return ValueTask.FromResult(0);
            case "list":
                foreach (var item in _pipeline.Describe())
                {
                    context.WriteObject(item);
                }

                return ValueTask.FromResult(0);
            default:
                context.WriteError($"Unknown subcommand '{args[0]}'. Usage: {Usage}");
                return ValueTask.FromResult(2);
        }
    }
}
