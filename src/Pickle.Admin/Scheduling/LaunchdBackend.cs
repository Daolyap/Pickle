using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Admin.Scheduling;

/// <summary>Per-user launchd agents (~/Library/LaunchAgents) on macOS. Pickle's own are labelled <c>com.pickle.&lt;name&gt;</c>.</summary>
internal sealed partial class LaunchdBackend(IProgramRunner runner, string? agentsDirectory = null, Func<int>? userId = null) : IJobBackend
{
    public const string LabelPrefix = "com.pickle.";
    private readonly string _directory = agentsDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents");
    private readonly Func<int> _uid = userId ?? (() => int.TryParse(Environment.GetEnvironmentVariable("UID"), CultureInfo.InvariantCulture, out var uid) ? uid : 501);

    public JobKind Kind => JobKind.Launchd;

    public bool IsAvailable => OperatingSystem.IsMacOS() && runner.Find("launchctl") is not null;

    public bool CanCreate => IsAvailable;

    public async Task<IReadOnlyList<ScheduledJob>> ListAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_directory))
        {
            return [];
        }

        var loaded = (await runner.RunAsync("launchctl", ["list"], null, cancellationToken).ConfigureAwait(false)).StdOut;
        var jobs = new List<ScheduledJob>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.plist").Order(StringComparer.Ordinal))
        {
            if (ParsePlist(File.ReadAllText(path)) is { } job)
            {
                jobs.Add(job with { Enabled = loaded.Contains(job.Id, StringComparison.Ordinal) && !job.Description!.Contains("Disabled", StringComparison.Ordinal) });
            }
        }

        return jobs;
    }

    public async Task<ServiceOperationResult> CreateAsync(NewJob job, CancellationToken cancellationToken)
    {
        var label = LabelPrefix + Slug(job.Name);
        var path = Path.Combine(_directory, label + ".plist");
        if (File.Exists(path))
        {
            return new ServiceOperationResult(false, $"{label} already exists.");
        }

        if (job.Command.Any(c => c is '\n' or '\r' or '\0') || string.IsNullOrWhiteSpace(job.Command))
        {
            throw new ArgumentException("The command must be one line.");
        }

        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(path, BuildPlist(label, job.Command, job.Trigger), cancellationToken).ConfigureAwait(false);
        var result = await runner.RunAsync("launchctl", ["bootstrap", $"gui/{_uid().ToString(CultureInfo.InvariantCulture)}", path], null, cancellationToken).ConfigureAwait(false);
        return new ServiceOperationResult(result.Success, result.Success ? $"Created {label}." : result.Message);
    }

    public async Task<ServiceOperationResult> RunNowAsync(ScheduledJob job, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync("launchctl", ["kickstart", Target(job)], null, cancellationToken).ConfigureAwait(false);
        return new ServiceOperationResult(result.Success, result.Success ? $"{job.Name} started." : result.Message);
    }

    public async Task<ServiceOperationResult> SetEnabledAsync(ScheduledJob job, bool enabled, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_directory, job.Id + ".plist");
        var domain = $"gui/{_uid().ToString(CultureInfo.InvariantCulture)}";
        var result = enabled
            ? await runner.RunAsync("launchctl", ["bootstrap", domain, path], null, cancellationToken).ConfigureAwait(false)
            : await runner.RunAsync("launchctl", ["bootout", Target(job)], null, cancellationToken).ConfigureAwait(false);
        return new ServiceOperationResult(result.Success, result.Success ? $"{job.Name} is {(enabled ? "on" : "off")}." : result.Message);
    }

    public async Task<ServiceOperationResult> DeleteAsync(ScheduledJob job, CancellationToken cancellationToken)
    {
        if (!job.Managed)
        {
            return new ServiceOperationResult(false, "Only agents Pickle created can be deleted here. Turn others off instead.");
        }

        await runner.RunAsync("launchctl", ["bootout", Target(job)], null, cancellationToken).ConfigureAwait(false);
        var path = Path.Combine(_directory, job.Id + ".plist");
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return new ServiceOperationResult(true, $"{job.Name} deleted.");
    }

    public string? HistoryCommand(ScheduledJob job) =>
        Label().IsMatch(job.Id) ? PowerShellQuote.Command("log", "show", "--last", "1h", "--predicate", $"process == \"launchd\" AND eventMessage CONTAINS \"{job.Id}\"") : null;

    public static string BuildPlist(string label, string command, TaskTriggerSpec trigger)
    {
        var dict = new XElement("dict",
            Key("Label"), new XElement("string", label),
            Key("ProgramArguments"), new XElement("array", new XElement("string", "/bin/sh"), new XElement("string", "-c"), new XElement("string", command)));
        var start = trigger.Start;
        XElement Calendar(params (string Key, int Value)[] entries) =>
            new("dict", entries.SelectMany(e => new XElement[] { Key(e.Key), new("integer", e.Value) }));

        switch (trigger.Kind)
        {
            case TaskTriggerKind.Daily when trigger.DaysInterval == 1:
                dict.Add(Key("StartCalendarInterval"), Calendar(("Hour", start?.Hour ?? 9), ("Minute", start?.Minute ?? 0)));
                break;
            case TaskTriggerKind.Weekly when trigger.WeeksInterval == 1 && trigger.DaysOfWeek is { Count: > 0 } days:
                dict.Add(Key("StartCalendarInterval"), new XElement("array", days.Select(d => Calendar(("Weekday", (int)d), ("Hour", start?.Hour ?? 9), ("Minute", start?.Minute ?? 0)))));
                break;
            case TaskTriggerKind.Monthly when trigger.DaysOfMonth is { Count: > 0 } monthDays:
                dict.Add(Key("StartCalendarInterval"), new XElement("array", monthDays.Select(d => Calendar(("Day", d), ("Hour", start?.Hour ?? 9), ("Minute", start?.Minute ?? 0)))));
                break;
            case TaskTriggerKind.Interval when trigger.RepeatEvery is { } every && every >= TimeSpan.FromMinutes(1):
                dict.Add(Key("StartInterval"), new XElement("integer", (long)every.TotalSeconds));
                break;
            case TaskTriggerKind.AtStartup or TaskTriggerKind.AtLogon:
                dict.Add(Key("RunAtLoad"), new XElement("true"));
                break;
            default:
                throw new NotSupportedException($"launchd can't express a '{trigger.Kind}' trigger here.");
        }

        var document = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"), dict));
        return document.Declaration + Environment.NewLine + document.DocumentType + Environment.NewLine + document.Root + Environment.NewLine;
    }

    public static ScheduledJob? ParsePlist(string xml)
    {
        try
        {
            var plist = XDocument.Parse(xml, LoadOptions.None).Root?.Element("dict");
            if (plist is null)
            {
                return null;
            }

            var values = new Dictionary<string, XElement>();
            var children = plist.Elements().ToList();
            for (var i = 0; i + 1 < children.Count; i += 2)
            {
                values[children[i].Value] = children[i + 1];
            }

            if (!values.TryGetValue("Label", out var labelElement) || !Label().IsMatch(labelElement.Value))
            {
                return null;
            }

            var label = labelElement.Value;
            var arguments = values.TryGetValue("ProgramArguments", out var args) ? args.Elements("string").Select(e => e.Value).ToList() : [];
            var command = arguments is ["/bin/sh", "-c", var script] ? script : string.Join(' ', arguments);
            var schedule = values.ContainsKey("StartInterval") ? $"every {values["StartInterval"].Value}s"
                : values.ContainsKey("StartCalendarInterval") ? "calendar"
                : values.TryGetValue("RunAtLoad", out var run) && run.Name == "true" ? "at login" : "(on demand)";
            return new ScheduledJob(label, label.StartsWith(LabelPrefix, StringComparison.Ordinal) ? label[LabelPrefix.Length..] : label, JobKind.Launchd, schedule, command, true)
            {
                Managed = label.StartsWith(LabelPrefix, StringComparison.Ordinal),
                Description = values.ContainsKey("Disabled") && values["Disabled"].Name == "true" ? "Disabled" : string.Empty,
            };
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static XElement Key(string name) => new("key", name);

    private string Target(ScheduledJob job)
    {
        if (!Label().IsMatch(job.Id))
        {
            throw new ArgumentException($"'{job.Id}' is not a launchd label.");
        }

        return $"gui/{_uid().ToString(CultureInfo.InvariantCulture)}/{job.Id}";
    }

    private static string Slug(string name)
    {
        var clean = Regex.Replace(name.Trim().ToLowerInvariant(), @"[^a-z0-9_-]+", "-").Trim('-');
        return clean.Length is > 0 and <= 40 ? clean : throw new ArgumentException("Give the agent a short name (letters, digits, - and _).");
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._@:-]{0,200}$")]
    private static partial Regex Label();
}
