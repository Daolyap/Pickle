using System.Globalization;
using System.Text;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Windows.TaskScheduler;

namespace Pickle.Windows.Commands;

/// <summary><c>pk schedule …</c> — Pickle commands on a schedule via Task Scheduler (tasks live in <c>\Pickle</c>).</summary>
internal sealed class ScheduleCommand : WindowsCommandBase
{
    public const string DefaultFolder = @"\Pickle";

    public override string Name => "schedule";

    public override string Description => "Schedule Pickle commands with Task Scheduler (list, add, remove, run, enable, disable, history)";

    public override string Usage =>
        "pk schedule list [folder] [--recurse]|add \"<when>\" <command...> [--name n] [--elevated]|remove <name>|run <name>|" +
        "enable <name>|disable <name>|history <name>   (when: " + ScheduleParser.Examples + ")";

    protected override async Task<int> RunAsync(CommandOutput output, IReadOnlyList<string> rawArgs, CancellationToken cancellationToken)
    {
        var args = CommandArgs.Parse(rawArgs, "name", "description", "max");
        if (args.Error is not null)
        {
            return UsageError(output, args.Error);
        }

        var scheduler = output.Pickle.Services.Get<ITaskSchedulerService>();
        if (scheduler is not { IsSupported: true })
        {
            output.Context.WriteError("Task Scheduler is only available on Windows.");
            return 1;
        }

        var sub = args.Arg(0)?.ToLowerInvariant();
        switch (sub)
        {
            case "list" or "ls":
                var folder = args.Arg(1) ?? DefaultFolder;
                var tasks = await scheduler.GetTasksAsync(folder, args.Has("recurse", "r"), cancellationToken).ConfigureAwait(false);
                output.Heading($"{tasks.Count} task(s) in {folder}");
                tasks.ToList().ForEach(output.Object);
                return 0;

            case "add" or "new" or "create":
                return await AddAsync(scheduler, output, args, cancellationToken).ConfigureAwait(false);

            case "remove" or "rm" or "delete":
                var removePath = ResolvePath(args.Arg(1));
                if (!output.Confirm(args, $"Delete scheduled task {removePath}?", defaultYes: false))
                {
                    output.Muted("Cancelled.");
                    return 1;
                }

                await scheduler.DeleteAsync(removePath, cancellationToken).ConfigureAwait(false);
                output.Success($"Deleted {removePath}");
                return 0;

            case "run":
                var runPath = ResolvePath(args.Arg(1));
                await scheduler.RunAsync(runPath, cancellationToken).ConfigureAwait(false);
                output.Success($"Started {runPath}");
                return 0;

            case "stop":
                var stopPath = ResolvePath(args.Arg(1));
                await scheduler.StopAsync(stopPath, cancellationToken).ConfigureAwait(false);
                output.Success($"Stopped {stopPath}");
                return 0;

            case "enable" or "disable":
                var path = ResolvePath(args.Arg(1));
                await scheduler.SetEnabledAsync(path, sub == "enable", cancellationToken).ConfigureAwait(false);
                output.Success($"{(sub == "enable" ? "Enabled" : "Disabled")} {path}");
                return 0;

            case "history":
                var historyPath = ResolvePath(args.Arg(1));
                var max = args.Value("max") is { } m && int.TryParse(m, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? Math.Min(n, 500) : 30;
                var runs = await scheduler.GetHistoryAsync(historyPath, max, cancellationToken).ConfigureAwait(false);
                output.Heading($"{runs.Count} history event(s) for {historyPath}");
                runs.ToList().ForEach(output.Object);
                return 0;

            case null:
                return UsageError(output);

            default:
                return UsageError(output, $"Unknown subcommand 'pk schedule {sub}'.");
        }
    }

    /// <summary>A bare name means <c>\Pickle\name</c>; a path starting with <c>\</c> is used as given.</summary>
    internal static string ResolvePath(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Give a task name (tasks created by Pickle live in \\Pickle) or a full path like \\Folder\\Task.");
        }

        var trimmed = name.Trim().Replace('/', '\\');
        if (trimmed.Length > 260 || trimmed.Any(char.IsControl))
        {
            throw new ArgumentException("The task path is invalid.");
        }

        if (trimmed.StartsWith('\\'))
        {
            return trimmed;
        }

        return WindowsIds.IsValidTaskName(trimmed)
            ? DefaultFolder + @"\" + trimmed
            : throw new ArgumentException($"Invalid task name '{trimmed}' (letters, digits, space . _ - ; max 64).");
    }

    internal static string DefaultName(string command, DateTimeOffset now)
    {
        var first = command.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "task";
        var sb = new StringBuilder();
        foreach (var c in first)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            {
                sb.Append(c);
            }

            if (sb.Length >= 24)
            {
                break;
            }
        }

        var stem = sb.Length > 0 && char.IsAsciiLetterOrDigit(sb[0]) ? sb.ToString() : "task";
        return $"{stem}-{now:yyyyMMdd-HHmmss}";
    }

    /// <summary>The task action: this Pickle executable with <c>-NoLogo -c "&lt;command&gt;"</c>.</summary>
    internal static TaskActionSpec BuildAction(string command, string? workingDirectory)
    {
        var program = Environment.ProcessPath ?? "pickle.exe";
        var arguments = "-NoLogo -c " + WindowsCommandLine.Quote(command);
        var cwd = workingDirectory is not null && Path.IsPathFullyQualified(workingDirectory) ? workingDirectory : null;
        return new TaskActionSpec(program, arguments, cwd);
    }

    private static async Task<int> AddAsync(ITaskSchedulerService scheduler, CommandOutput output, CommandArgs args, CancellationToken cancellationToken)
    {
        var when = args.Arg(1);
        var command = args.Rest(2).Trim();
        if (string.IsNullOrWhiteSpace(when) || command.Length == 0)
        {
            output.Context.WriteError("pk schedule add needs a schedule and a command, e.g. pk schedule add \"daily 09:00\" pk upgrade --yes");
            output.Muted("schedules: " + ScheduleParser.Examples);
            return 2;
        }

        var trigger = scheduler.ParseSchedule(when);
        var name = args.Value("name") ?? DefaultName(command, DateTimeOffset.Now);
        if (!WindowsIds.IsValidTaskName(name))
        {
            throw new ArgumentException($"Invalid task name '{name}' (letters, digits, space . _ - ; max 64).");
        }

        var elevated = args.Has("elevated", "admin") || trigger.Kind == TaskTriggerKind.AtStartup;
        var definition = new ScheduledTaskDefinition(
            name,
            trigger,
            BuildAction(command, output.Context.Cwd),
            DefaultFolder,
            args.Value("description") ?? "Pickle: " + (command.Length > 200 ? command[..200] : command),
            RunElevated: elevated);
        TaskDefinitionCodec.Validate(definition, elevated: false);

        var broker = output.Pickle.Services.Get<IElevationBroker>();
        if (elevated && broker is { IsElevated: false })
        {
            var question = $"Create task {DefaultFolder}\\{name} ({ScheduleParser.Describe(trigger)}) running with highest privileges? " +
                "This needs administrator rights (UAC prompt).";
            if (!output.Confirm(args, question, defaultYes: true))
            {
                output.Muted("Cancelled.");
                return 1;
            }
        }

        var info = await scheduler.CreateAsync(definition, cancellationToken).ConfigureAwait(false);
        output.Success($"Created {info.Path} — {ScheduleParser.Describe(trigger)}{(info.NextRunTime is { } next ? ", next run " + CommandOutput.When(next) : string.Empty)}");
        output.Object(info);
        return 0;
    }
}
