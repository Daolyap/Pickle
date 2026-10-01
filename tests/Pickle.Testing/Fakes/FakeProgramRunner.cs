using Pickle.Abstractions.Services;

namespace Pickle.Testing.Fakes;

/// <summary>
/// A scripted <see cref="IProgramRunner"/>: register what a program prints with <see cref="On"/> and read the calls back
/// from <see cref="Calls"/>. A program that was never set up (and <see cref="Installed"/> does not list) is "not found".
/// </summary>
public sealed class FakeProgramRunner : IProgramRunner
{
    private readonly List<(Func<string, IReadOnlyList<string>, bool> Match, Func<IReadOnlyList<string>, ProgramRunOptions?, ProgramResult> Respond)> _rules = [];

    /// <summary>Programs that "exist" even without a rule.</summary>
    public HashSet<string> Installed { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<FakeProgramCall> Calls { get; } = [];

    /// <summary>Lines <see cref="StreamAsync"/> emits (then it waits for cancellation unless <see cref="StreamEnds"/>).</summary>
    public List<string> StreamLines { get; } = [];

    public bool StreamEnds { get; set; } = true;

    public string? Find(string program) =>
        Installed.Contains(Path.GetFileName(program)) || _rules.Any(r => r.Match(program, [])) ? "/usr/bin/" + Path.GetFileName(program) : null;

    /// <summary>When <paramref name="program"/> is run with arguments starting with <paramref name="argumentPrefix"/>, answer with <paramref name="stdout"/>.</summary>
    public FakeProgramRunner On(string program, string argumentPrefix, string stdout, int exitCode = 0, string stderr = "")
    {
        var prefix = argumentPrefix.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _rules.Insert(0, (
            (p, a) => SameProgram(p, program) && (a.Count == 0 || StartsWith(a, prefix)),
            (_, _) => new ProgramResult(exitCode, stdout, stderr)));
        Installed.Add(program);
        return this;
    }

    public FakeProgramRunner On(string program, Func<IReadOnlyList<string>, ProgramRunOptions?, ProgramResult?> respond)
    {
        _rules.Insert(0, (
            (p, _) => SameProgram(p, program),
            (a, o) => respond(a, o) ?? new ProgramResult(1, string.Empty, "unexpected call: " + string.Join(' ', a))));
        Installed.Add(program);
        return this;
    }

    public Task<ProgramResult> RunAsync(string program, IEnumerable<string> arguments, ProgramRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        var args = arguments.ToList();
        Calls.Add(new FakeProgramCall(program, args, options));
        foreach (var (match, respond) in _rules)
        {
            if (match(program, args))
            {
                return Task.FromResult(respond(args, options));
            }
        }

        return Task.FromResult(new ProgramResult(ProgramResult.NotFound, string.Empty, program + " was not found on PATH."));
    }

    public async Task<int> StreamAsync(string program, IEnumerable<string> arguments, Action<string> onLine, ProgramRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls.Add(new FakeProgramCall(program, [.. arguments], options));
        foreach (var line in StreamLines)
        {
            onLine(line);
        }

        if (!StreamEnds)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        return 0;
    }

    /// <summary>The recorded calls for one program, as "arg arg arg" strings.</summary>
    public IReadOnlyList<string> CommandLines(string program) =>
        [.. Calls.Where(c => SameProgram(c.Program, program)).Select(c => string.Join(' ', c.Arguments))];

    // Callers pass what Find returned (an absolute path) as often as a bare name.
    private static bool SameProgram(string called, string registered) =>
        string.Equals(Path.GetFileName(called), Path.GetFileName(registered), StringComparison.OrdinalIgnoreCase);

    private static bool StartsWith(IReadOnlyList<string> arguments, string[] prefix) =>
        arguments.Count >= prefix.Length && prefix.Select((p, i) => arguments[i] == p).All(x => x);
}

public sealed record FakeProgramCall(string Program, IReadOnlyList<string> Arguments, ProgramRunOptions? Options);
