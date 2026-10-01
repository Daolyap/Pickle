using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Tui.Panels.Admin.EnvEditor;

/// <summary><c>pk hosts</c>: the hosts file as a panel (interactive) or as objects and one-line edits (scripts).</summary>
internal sealed class HostsCommand : PanelCommand
{
    public override string Name => "hosts";

    public override string Description => "View and edit the hosts file (privileged write only when applying)";

    public override string Usage => "pk hosts | list | add <address> <name>… [--comment text] | remove <name> | enable <name> | disable <name> [--yes]";

    public override IReadOnlyList<string> Examples =>
    [
        "pk hosts add 10.0.0.5 nas nas.lan --comment 'file server'",
        "pk hosts disable nas",
    ];

    protected override string PanelId => EnvironmentPanel.PanelId;

    protected override string? PanelArgument(IReadOnlyList<string> args) => "hosts";

    protected override async Task<int> RunAsync(CommandOutput output, IReadOnlyList<string> raw, CancellationToken cancellationToken)
    {
        var verb = raw.Count > 0 ? raw[0].ToLowerInvariant() : string.Empty;
        if (verb is not ("add" or "remove" or "rm" or "enable" or "disable"))
        {
            return await base.RunAsync(output, raw, cancellationToken).ConfigureAwait(false);
        }

        var args = CommandArgs.Parse(raw.Skip(1).ToList(), "comment");
        var hosts = output.Pickle.Services.Require<IHostsService>();
        var document = await hosts.ReadAsync(cancellationToken).ConfigureAwait(false);
        var entries = document.Entries;
        switch (verb)
        {
            case "add":
                if (args.Arg(0) is not { } address || args.Positional.Count < 2)
                {
                    return UsageError(output, "Give an address and at least one name.");
                }

                document.Add(new HostsEntry(address, [.. args.Positional.Skip(1)], args.Value("comment"), true));
                break;
            default:
                if (args.Arg(0) is not { } name)
                {
                    return UsageError(output, $"Name the host to {verb}.");
                }

                var matches = Enumerable.Range(0, entries.Count).Where(i => entries[i].Names.Contains(name, StringComparer.OrdinalIgnoreCase)).Reverse().ToList();
                if (matches.Count == 0)
                {
                    output.Failure($"No entry for {name}.");
                    return 1;
                }

                foreach (var index in matches)
                {
                    if (verb is "remove" or "rm")
                    {
                        document.Remove(index);
                    }
                    else
                    {
                        document.SetEnabled(index, verb == "enable");
                    }
                }

                break;
        }

        if (!output.Confirm(args, $"Apply this change to {hosts.Path}? (asks for administrator or root rights)", true))
        {
            output.Muted("Cancelled.");
            return 1;
        }

        var result = await hosts.WriteAsync(document, cancellationToken).ConfigureAwait(false);
        return Report(output, result);
    }

    protected override async Task<int> ListAsync(CommandOutput output, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var document = await output.Pickle.Services.Require<IHostsService>().ReadAsync(cancellationToken).ConfigureAwait(false);
        foreach (var entry in document.Entries)
        {
            output.Object(Display.Columns(new HostRow(entry.Address, string.Join(' ', entry.Names), entry.Enabled, entry.Comment), "Address", "Names", "Enabled", "Comment"));
        }

        return 0;
    }

    internal static int Report(CommandOutput output, ServiceOperationResult result)
    {
        if (result.Success)
        {
            output.Success(result.Message);
            return 0;
        }

        output.Failure(result.Message);
        if (result.ShellCommand is { } command)
        {
            output.Muted("It needs a password. Run: " + command);
        }

        return 1;
    }

    private sealed record HostRow(string Address, string Names, bool Enabled, string? Comment);
}

/// <summary><c>pk env</c> and <c>pk path</c>: variables and PATH of the Pickle, user, machine or process scope.</summary>
internal sealed class EnvCommand(bool pathOnly) : PanelCommand
{
    public override string Name => pathOnly ? "path" : "env";

    public override string Description => pathOnly ? "View and edit PATH (Pickle, user or machine scope)" : "View and edit environment variables (Pickle, user or machine scope)";

    public override string Usage => pathOnly
        ? "pk path | list | add <folder> [--first] | remove <folder> [--scope pickle|user|machine] [--yes]"
        : "pk env | list | get <name> | set <name> <value> | unset <name> [--scope pickle|user|machine|process] [--yes]";

    public override IReadOnlyList<string> Examples => pathOnly
        ? ["pk path add ~/bin --scope pickle", "pk path remove C:\\old --scope user"]
        : ["pk env set EDITOR vim --scope user", "pk env list --scope machine"];

    protected override string PanelId => EnvironmentPanel.PanelId;

    protected override string? PanelArgument(IReadOnlyList<string> args) => pathOnly ? "path" : "variables";

    protected override async Task<int> RunAsync(CommandOutput output, IReadOnlyList<string> raw, CancellationToken cancellationToken)
    {
        var verb = raw.Count > 0 ? raw[0].ToLowerInvariant() : string.Empty;
        var handled = pathOnly ? verb is "add" or "remove" or "rm" : verb is "get" or "set" or "unset";
        if (!handled)
        {
            return await base.RunAsync(output, raw, cancellationToken).ConfigureAwait(false);
        }

        var args = CommandArgs.Parse(raw.Skip(1).ToList(), "scope");
        var store = output.Pickle.Services.Require<IEnvironmentStore>();
        var scope = ParseScope(args.Value("scope"), store);
        if (args.Arg(0) is not { } first)
        {
            return UsageError(output, "Name what to change.");
        }

        if (pathOnly)
        {
            var list = new PathList(await store.GetPathAsync(scope, cancellationToken).ConfigureAwait(false));
            if (verb == "add")
            {
                list.Add(first, args.Has("first") ? 0 : null);
            }
            else
            {
                var index = list.Items.ToList().FindIndex(e => string.Equals(e, first, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                if (index < 0)
                {
                    output.Failure($"{first} is not on the {scope} PATH.");
                    return 1;
                }

                list.RemoveAt(index);
            }

            return Confirmed(output, args, store, scope) ? HostsCommand.Report(output, await store.SetPathAsync(scope, list.Items, cancellationToken).ConfigureAwait(false)) : Cancelled(output);
        }

        switch (verb)
        {
            case "get":
                var value = (await store.ListAsync(scope, cancellationToken).ConfigureAwait(false)).FirstOrDefault(v => string.Equals(v.Name, first, StringComparison.OrdinalIgnoreCase));
                if (value is null)
                {
                    output.Failure($"{first} is not set in the {scope} scope.");
                    return 1;
                }

                output.Object(value.Value);
                return 0;
            case "set":
                if (args.Positional.Count < 2)
                {
                    return UsageError(output, "Give a name and a value.");
                }

                return Confirmed(output, args, store, scope) ? HostsCommand.Report(output, await store.SetAsync(scope, first, args.Rest(1), cancellationToken).ConfigureAwait(false)) : Cancelled(output);
            default:
                return Confirmed(output, args, store, scope) ? HostsCommand.Report(output, await store.SetAsync(scope, first, null, cancellationToken).ConfigureAwait(false)) : Cancelled(output);
        }
    }

    protected override async Task<int> ListAsync(CommandOutput output, IReadOnlyList<string> raw, CancellationToken cancellationToken)
    {
        var args = CommandArgs.Parse(raw, "scope");
        var store = output.Pickle.Services.Require<IEnvironmentStore>();
        var scope = ParseScope(args.Value("scope"), store);
        if (pathOnly)
        {
            var list = new PathList(await store.GetPathAsync(scope, cancellationToken).ConfigureAwait(false));
            for (var i = 0; i < list.Items.Count; i++)
            {
                output.Object(Display.Columns(new PathRow(i + 1, list.Items[i], list.Problem(i, Directory.Exists, Environment.ExpandEnvironmentVariables)), "Position", "Path", "Problem"));
            }

            return 0;
        }

        foreach (var variable in await store.ListAsync(scope, cancellationToken).ConfigureAwait(false))
        {
            output.Object(Display.Columns(variable, "Name", "Value", "Scope"));
        }

        return 0;
    }

    private static bool Confirmed(CommandOutput output, CommandArgs args, IEnvironmentStore store, EnvironmentScope scope) =>
        !store.NeedsPrivileges(scope) || output.Confirm(args, "This changes the machine-wide setting and asks for administrator or root rights. Continue?", true);

    private static int Cancelled(CommandOutput output)
    {
        output.Muted("Cancelled.");
        return 1;
    }

    private static EnvironmentScope ParseScope(string? value, IEnvironmentStore store)
    {
        if (value is null)
        {
            return store.Scopes.Contains(EnvironmentScope.User) ? EnvironmentScope.User : EnvironmentScope.Pickle;
        }

        if (!Enum.TryParse<EnvironmentScope>(value, ignoreCase: true, out var scope) || !store.Scopes.Contains(scope))
        {
            throw new ArgumentException($"Scope '{value}' is not available here. Choose from: {string.Join(", ", store.Scopes.Select(s => s.ToString().ToLowerInvariant()))}.");
        }

        return scope;
    }

    private sealed record PathRow(int Position, string Path, string? Problem);
}
