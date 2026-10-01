using System.Text;
using System.Text.RegularExpressions;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Admin.Scheduling;

/// <summary>The current user's crontab. Entries Pickle creates are tagged with a <c># pickle:name</c> line; a job switched off is commented with <c>#pickle-off# </c>.</summary>
internal sealed partial class CronBackend(IProgramRunner runner, Func<DateTimeOffset> now) : IJobBackend
{
    public const string OffPrefix = "#pickle-off# ";
    public const string Tag = "# pickle:";

    public JobKind Kind => JobKind.Cron;

    public bool IsAvailable => runner.Find("crontab") is not null;

    public bool CanCreate => true;

    public async Task<IReadOnlyList<ScheduledJob>> ListAsync(CancellationToken cancellationToken) =>
        Parse(await ReadAsync(cancellationToken).ConfigureAwait(false), now()).Jobs;

    public async Task<ServiceOperationResult> CreateAsync(NewJob job, CancellationToken cancellationToken)
    {
        var name = Name(job.Name);
        var expression = CronExpression.FromTrigger(job.Trigger);
        RequireSingleLine(job.Command);
        var text = await ReadAsync(cancellationToken).ConfigureAwait(false);
        var document = Parse(text, now());
        if (document.Jobs.Any(j => j.Managed && j.Name == name))
        {
            return new ServiceOperationResult(false, $"A Pickle job called '{name}' already exists in your crontab.");
        }

        var lines = document.Lines.ToList();
        if (lines.Count > 0 && lines[^1].Length > 0)
        {
            lines.Add(string.Empty);
        }

        lines.Add(Tag + name);
        lines.Add($"{expression} {Escape(job.Command)}");
        var described = CronExpression.TryParse(expression, out var parsed) ? parsed.Describe() : expression;
        return await WriteAsync(lines, $"Added '{name}' ({described}) to your crontab.", cancellationToken).ConfigureAwait(false);
    }

    public async Task<ServiceOperationResult> RunNowAsync(ScheduledJob job, CancellationToken cancellationToken)
    {
        var shell = runner.Find("sh") ?? throw new InvalidOperationException("sh was not found.");
        var result = await runner.RunAsync(shell, ["-c", job.Command], new ProgramRunOptions { Timeout = TimeSpan.FromMinutes(10), WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) }, cancellationToken).ConfigureAwait(false);
        return new ServiceOperationResult(result.Success, result.Success ? $"{job.Name} ran. {Last(result.StdOut)}".Trim() : result.Message);
    }

    public async Task<ServiceOperationResult> SetEnabledAsync(ScheduledJob job, bool enabled, CancellationToken cancellationToken)
    {
        var document = Parse(await ReadAsync(cancellationToken).ConfigureAwait(false), now());
        var index = Locate(document, job);
        if (index < 0)
        {
            return new ServiceOperationResult(false, "That crontab line is gone; refresh the list.");
        }

        var lines = document.Lines.ToList();
        lines[index] = enabled ? lines[index][OffPrefix.Length..] : OffPrefix + lines[index];
        return await WriteAsync(lines, $"{job.Name} is {(enabled ? "on" : "off")}.", cancellationToken).ConfigureAwait(false);
    }

    public async Task<ServiceOperationResult> DeleteAsync(ScheduledJob job, CancellationToken cancellationToken)
    {
        var document = Parse(await ReadAsync(cancellationToken).ConfigureAwait(false), now());
        var index = Locate(document, job);
        if (index < 0)
        {
            return new ServiceOperationResult(false, "That crontab line is gone; refresh the list.");
        }

        var lines = document.Lines.ToList();
        lines.RemoveAt(index);
        if (index > 0 && lines[index - 1].StartsWith(Tag, StringComparison.Ordinal))
        {
            lines.RemoveAt(index - 1);
        }

        return await WriteAsync(lines, $"{job.Name} removed from your crontab.", cancellationToken).ConfigureAwait(false);
    }

    public string? HistoryCommand(ScheduledJob job) =>
        PowerShellQuote.Command("journalctl", "--no-pager", "-n", "50", "-t", "CRON");

    public sealed record Document(IReadOnlyList<string> Lines, IReadOnlyList<ScheduledJob> Jobs, IReadOnlyDictionary<string, int> LineOf);

    public static Document Parse(string text, DateTimeOffset now)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n').Where((l, i) => i > 0 || l.Length > 0).ToList();
        var jobs = new List<ScheduledJob>();
        var lineOf = new Dictionary<string, int>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var enabled = !line.StartsWith(OffPrefix, StringComparison.Ordinal);
            var body = enabled ? line : line[OffPrefix.Length..];
            if (body.Length == 0 || body.TrimStart().StartsWith('#') || EnvironmentAssignment().IsMatch(body))
            {
                continue;
            }

            var fields = SplitJob(body);
            if (fields is null || !CronExpression.TryParse(fields.Value.Schedule, out var expression))
            {
                continue;
            }

            var managed = i > 0 && lines[i - 1].StartsWith(Tag, StringComparison.Ordinal);
            var name = managed ? lines[i - 1][Tag.Length..].Trim() : $"line {i + 1}: {FirstWord(fields.Value.Command)}";
            var id = "cron:" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            lineOf[id] = i;
            jobs.Add(new ScheduledJob(id, name, JobKind.Cron, expression.Describe(), Unescape(fields.Value.Command), enabled)
            {
                NextRun = enabled ? expression.Next(now) : null,
                Managed = managed,
                Description = expression.Text,
            });
        }

        return new Document(lines, jobs, lineOf);
    }

    private static int Locate(Document document, ScheduledJob job) =>
        document.LineOf.TryGetValue(job.Id, out var index) && document.Jobs.Any(j => j.Id == job.Id && j.Command == job.Command) ? index : -1;

    private static (string Schedule, string Command)? SplitJob(string body)
    {
        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('@'))
        {
            var parts = trimmed.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 2 ? (parts[0], parts[1].Trim()) : null;
        }

        var fields = trimmed.Split([' ', '\t'], 6, StringSplitOptions.RemoveEmptyEntries);
        return fields.Length == 6 ? (string.Join(' ', fields[..5]), fields[5].Trim()) : null;
    }

    private async Task<string> ReadAsync(CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync("crontab", ["-l"], null, cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            return result.StdOut;
        }

        return result.StdErr.Contains("no crontab", StringComparison.OrdinalIgnoreCase) ? string.Empty : throw new InvalidOperationException(result.Message);
    }

    private async Task<ServiceOperationResult> WriteAsync(IReadOnlyList<string> lines, string message, CancellationToken cancellationToken)
    {
        var text = string.Join('\n', lines) + "\n";
        var result = await runner.RunAsync("crontab", ["-"], new ProgramRunOptions { StandardInput = text }, cancellationToken).ConfigureAwait(false);
        return new ServiceOperationResult(result.Success, result.Success ? message : result.Message);
    }

    private static string Name(string name)
    {
        var clean = Regex.Replace(name.Trim().ToLowerInvariant(), @"[^a-z0-9_-]+", "-").Trim('-');
        return clean.Length is > 0 and <= 40 ? clean : throw new ArgumentException("Give the job a short name (letters, digits, - and _).");
    }

    private static void RequireSingleLine(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Any(c => c is '\n' or '\r' or '\0'))
        {
            throw new ArgumentException("The command must be one line.");
        }
    }

    // In a crontab a bare % starts stdin data, so a literal one is written \%.
    private static string Escape(string command) => command.Replace("%", "\\%", StringComparison.Ordinal);

    private static string Unescape(string command) => command.Replace("\\%", "%", StringComparison.Ordinal);

    private static string FirstWord(string command) => Path.GetFileName(command.Split(' ', 2)[0]);

    private static string Last(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? string.Empty;

    [GeneratedRegex(@"^\s*[A-Za-z_][A-Za-z0-9_]*\s*=")]
    private static partial Regex EnvironmentAssignment();
}
