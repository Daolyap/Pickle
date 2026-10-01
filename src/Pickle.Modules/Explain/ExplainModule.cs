using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Tui;

namespace Pickle.Modules.Explain;

/// <summary>
/// "What does this command do?" without a network or an account: <c>pk explain tar -xzvf a.tgz</c>, or Alt+Shift+E on the
/// line you are typing. Knowledge comes from an embedded pack (about 70 commands with their flags and subcommands) that
/// you can extend with JSON files in the <c>explain</c> folder of the data directory, and from PowerShell itself for
/// aliases and cmdlets.
/// </summary>
public sealed class ExplainModule : IPicklePlugin
{
    public const string ModuleId = "explain";

    public static ModuleDescriptor Descriptor { get; } = new()
    {
        Id = ModuleId,
        Name = "Command explainer",
        Description = "pk explain and Alt+Shift+E: what each part of a command line does, offline, with warnings for dangerous ones",
        Create = () => new ExplainModule(),
        Provides = ["pk explain", "panel: explain (Alt+Shift+E)"],
    };

    string IPicklePlugin.Id => "pickle." + ModuleId;

    public string DisplayName => Descriptor.Name;

    public string Description => Descriptor.Description;

    public void Initialize(IPickleContext context)
    {
        context.Panels.Register(ExplainPanel.Descriptor);
        context.Commands.Register(new ExplainCommand());
        context.KeyBindings.RegisterAction("explain.line", "Explain the command line being typed", (buffer, _) =>
        {
            var line = buffer.Text.Trim().Length > 0 ? buffer.Text : context.History.Entries.LastOrDefault()?.CommandLine ?? string.Empty;
            if (line.Length > 0)
            {
                buffer.ShowPanel(ExplainPanel.PanelId, line);
            }

            return ValueTask.CompletedTask;
        });
        context.KeyBindings.Bind("Alt+Shift+E", "explain.line");
    }

    internal static CommandExplainer CreateExplainer(IPickleContext pickle) =>
        new(ExplainPack.Load(Path.Combine(pickle.Paths.DataDir, "explain"), (file, ex) => pickle.Log.Warn("explain", $"{file} is not a valid explain pack: {ex.Message}")), (name, ct) => DescribeAsync(pickle, name, ct));

    private static async Task<ShellCommandInfo?> DescribeAsync(IPickleContext pickle, string name, CancellationToken cancellationToken)
    {
        const string script = """
            param($Name)
            $command = Get-Command -Name $Name -ErrorAction SilentlyContinue | Select-Object -First 1
            if (-not $command) { return }
            $target = if ($command.CommandType -eq 'Alias' -and $command.ResolvedCommand) { $command.ResolvedCommand } else { $command }
            $parameters = @()
            if ($target.Parameters) { $parameters = @($target.Parameters.GetEnumerator() | ForEach-Object { '{0}|{1}' -f $_.Key, $_.Value.ParameterType.Name }) }
            [pscustomobject]@{ Kind = [string]$command.CommandType; Resolved = if ($command.CommandType -eq 'Alias') { $target.Name } else { $null }; Module = [string]$target.ModuleName; Parameters = $parameters }
            """;
        var result = await pickle.Shell.InvokeAsync(script, new Dictionary<string, object?> { ["Name"] = name }, ShellTarget.Main, cancellationToken).ConfigureAwait(false);
        if (result.Output.FirstOrDefault() is not { } row)
        {
            return null;
        }

        string? Text(string property) => row.Properties[property]?.Value?.ToString() is { Length: > 0 } value ? value : null;
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (row.Properties["Parameters"]?.Value is System.Collections.IEnumerable list)
        {
            foreach (var item in list.OfType<object>().Select(o => o.ToString() ?? string.Empty))
            {
                var parts = item.Split('|', 2);
                if (parts.Length == 2)
                {
                    parameters[parts[0]] = parts[1];
                }
            }
        }

        return new ShellCommandInfo(Text("Kind") ?? "Command", Text("Resolved"), Text("Module"), parameters);
    }
}

/// <summary><c>pk explain &lt;command line&gt;</c>.</summary>
internal sealed class ExplainCommand : PickleCommandBase
{
    public override string Name => "explain";

    public override string Description => "Explain what a command line does, part by part (offline)";

    public override string Usage => "pk explain [command line…]    (quote it if it has pipes or ';'; no argument: the last command you ran)";

    public override IReadOnlyList<string> Examples => ["pk explain tar -xzvf backup.tgz -C /tmp", "pk explain 'find . -name \"*.log\" -mtime +7 -delete'", "pk explain"];

    protected override async Task<int> RunAsync(CommandOutput output, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var line = args.Count > 0 ? string.Join(' ', args) : output.Pickle.History.Entries.LastOrDefault(e => !e.CommandLine.StartsWith("pk explain", StringComparison.OrdinalIgnoreCase))?.CommandLine;
        if (string.IsNullOrWhiteSpace(line))
        {
            return UsageError(output, "Give a command line to explain.");
        }

        var explanation = await ExplainModule.CreateExplainer(output.Pickle).ExplainAsync(line, cancellationToken).ConfigureAwait(false);
        foreach (var warning in explanation.Warnings)
        {
            output.Warning(warning);
        }

        foreach (var part in explanation.Parts)
        {
            output.Object(Display.Columns(new PartRow(new string(' ', part.Depth * 2) + part.Text, part.Meaning, part.Kind.ToString()), "Part", "Meaning"));
        }

        return 0;
    }

    private sealed record PartRow(string Part, string Meaning, string Kind);
}

/// <summary>Alt+Shift+E: the line split into its parts with what each does; warnings come first.</summary>
internal sealed class ExplainPanel : ResourcePanel<ExplainedPart>
{
    public const string PanelId = "explain";

    private readonly string _line;

    public static PanelDescriptor Descriptor { get; } = new()
    {
        Id = PanelId,
        Title = "Explain",
        Description = "What each part of a command line does",
        CreateView = context => new ExplainPanel(context),
    };

    public ExplainPanel(PanelContext context)
        : base(context, "Explain", p => p.Text, "Meaning")
    {
        _line = context.Argument ?? context.CurrentInput ?? string.Empty;
        PanelTitle = "Explain · " + (_line.Length <= 60 ? _line : _line[..57] + "…");
        AddCommand(Terminal.Gui.Input.Key.F2, "Put it back", _ => _line, PanelResultKind.ReplaceInput);
    }

    protected override string EmptyMessage => "Nothing to explain: type a command first.";

    protected override async Task<IReadOnlyList<ExplainedPart>> LoadAsync(CancellationToken cancellationToken)
    {
        var explanation = await ExplainModule.CreateExplainer(Pickle).ExplainAsync(_line, cancellationToken).ConfigureAwait(false);
        return [.. explanation.Warnings.Select(w => new ExplainedPart("⚠ careful", w, PartKind.Note)), .. explanation.Parts];
    }

    protected override Task<IReadOnlyList<string>> DescribeAsync(ExplainedPart item, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([item.Text, string.Empty, .. Wrap(item.Meaning, 70)]);

    protected override string KeyOf(ExplainedPart item) => $"{item.Depth}|{item.Kind}|{item.Text}";

    protected override string? Hint(ExplainedPart item) => item.Kind == PartKind.Note ? null : item.Kind.ToString().ToLowerInvariant();

    protected override string? Detail(ExplainedPart item) => item.Meaning.Length <= 90 ? item.Meaning : item.Meaning[..87] + "…";

    protected override Terminal.Gui.Drawing.Color? ItemColor(ExplainedPart item) => item.Kind switch
    {
        PartKind.Note => Schemes.Warning.Foreground,
        PartKind.Command => Schemes.Accent.Foreground,
        PartKind.Operator or PartKind.Redirect => Schemes.Info.Foreground,
        _ => null,
    };

    protected override void OnAccepted(ExplainedPart item)
    {
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        var line = text;
        while (line.Length > width)
        {
            var cut = line.LastIndexOf(' ', width);
            cut = cut <= 0 ? width : cut;
            yield return line[..cut];
            line = line[cut..].TrimStart();
        }

        yield return line;
    }
}
