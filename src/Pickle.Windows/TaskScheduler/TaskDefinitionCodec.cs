using System.Text.Json;
using System.Text.Json.Serialization;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Windows.TaskScheduler;

/// <summary>
/// Serializes <see cref="ScheduledTaskDefinition"/> for the elevated helper and validates definitions before they are
/// registered (strictly when elevated: only under <c>\Pickle</c>, absolute .exe program, bounded sizes).
/// </summary>
public static class TaskDefinitionCodec
{
    public const int MaxJsonLength = 32 * 1024;
    public const int MaxArgumentsLength = 8 * 1024;
    public const int MaxDescriptionLength = 1024;

    private static readonly JsonSerializerOptions Strict = new(PickleJson.Compact)
    {
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
    };

    public static string Serialize(ScheduledTaskDefinition definition) => JsonSerializer.Serialize(definition, Strict);

    public static ScheduledTaskDefinition Deserialize(string json)
    {
        if (json.Length > MaxJsonLength)
        {
            throw new ArgumentException("Task definition is too large.");
        }

        try
        {
            return JsonSerializer.Deserialize<ScheduledTaskDefinition>(json, Strict)
                ?? throw new ArgumentException("Task definition is empty.");
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Task definition is malformed: " + ex.Message, ex);
        }
    }

    /// <summary>Throws <see cref="ArgumentException"/> describing the first problem.</summary>
    public static void Validate(ScheduledTaskDefinition definition, bool elevated)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!WindowsIds.IsValidTaskName(definition.Name))
        {
            throw new ArgumentException($"Invalid task name '{definition.Name}' (letters, digits, space . _ - ; max 64).");
        }

        if (!WindowsIds.IsValidTaskFolder(definition.Folder))
        {
            throw new ArgumentException($"Invalid task folder '{definition.Folder}'.");
        }

        if (elevated && !WindowsIds.IsPickleTaskFolder(definition.Folder))
        {
            throw new ArgumentException(@"Elevated tasks can only be registered under \Pickle.");
        }

        if (definition.Description is { } description && (description.Length > MaxDescriptionLength || HasControl(description)))
        {
            throw new ArgumentException("The task description is too long or contains control characters.");
        }

        ValidateAction(definition.Action ?? throw new ArgumentException("The task has no action."), elevated);
        ValidateTrigger(definition.Trigger ?? throw new ArgumentException("The task has no trigger."));
    }

    private static void ValidateAction(TaskActionSpec action, bool elevated)
    {
        if (string.IsNullOrWhiteSpace(action.Program) || action.Program.Length > 260 || HasControl(action.Program) || action.Program.Contains('"'))
        {
            throw new ArgumentException("The task program path is invalid.");
        }

        if (elevated && (!Path.IsPathFullyQualified(action.Program) || !action.Program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Elevated tasks must run an .exe given by its full path.");
        }

        if (action.Arguments is { } args && (args.Length > MaxArgumentsLength || HasControl(args)))
        {
            throw new ArgumentException("The task arguments are too long or contain control characters.");
        }

        if (action.WorkingDirectory is { } dir && (dir.Length > 260 || HasControl(dir) || !Path.IsPathFullyQualified(dir)))
        {
            throw new ArgumentException("The task working directory must be a full path.");
        }
    }

    private static void ValidateTrigger(TaskTriggerSpec trigger)
    {
        if (!Enum.IsDefined(trigger.Kind))
        {
            throw new ArgumentException("Unknown trigger kind.");
        }

        var needsStart = trigger.Kind is TaskTriggerKind.Once or TaskTriggerKind.Daily or TaskTriggerKind.Weekly or TaskTriggerKind.Monthly or TaskTriggerKind.Interval;
        if (needsStart && trigger.Start is null)
        {
            throw new ArgumentException($"A {trigger.Kind} trigger needs a start time.");
        }

        if (trigger.DaysInterval is < 1 or > 365 || trigger.WeeksInterval is < 1 or > 52)
        {
            throw new ArgumentException("The trigger interval is out of range.");
        }

        if (trigger.Kind == TaskTriggerKind.Weekly
            && (trigger.DaysOfWeek is not { Count: >= 1 and <= 7 } days || days.Any(d => !Enum.IsDefined(d))))
        {
            throw new ArgumentException("A weekly trigger needs 1-7 valid days.");
        }

        if (trigger.Kind == TaskTriggerKind.Monthly
            && (trigger.DaysOfMonth is not { Count: >= 1 and <= 31 } monthDays || monthDays.Any(d => d is < 1 or > 31)))
        {
            throw new ArgumentException("A monthly trigger needs days between 1 and 31.");
        }

        if (trigger.Kind == TaskTriggerKind.Interval
            && (trigger.RepeatEvery is not { } every || every < TimeSpan.FromMinutes(1) || every > TimeSpan.FromDays(31)))
        {
            throw new ArgumentException("A repeating trigger needs an interval between 1 minute and 31 days.");
        }

        if (trigger.RepeatDuration is { } duration && (duration < TimeSpan.Zero || duration > TimeSpan.FromDays(366)))
        {
            throw new ArgumentException("The repetition duration is out of range.");
        }
    }

    private static bool HasControl(string value) => value.Any(char.IsControl);
}
