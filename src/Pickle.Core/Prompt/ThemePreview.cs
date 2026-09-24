using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Core.Contracts;
using Pickle.Core.Prompt.Segments;

namespace Pickle.Core.Prompt;

/// <summary>Fixed sample data for `pk theme preview` and prompt snapshot tests: every built-in segment is visible.</summary>
public sealed class ThemePreview
{
    private readonly Dictionary<string, IPromptSegment> _segments;

    public ThemePreview(bool windowsPaths)
    {
        Home = windowsPaths ? @"C:\Users\pickle" : "/home/pickle";
        var sep = windowsPaths ? @"\" : "/";
        RepositoryRoot = Home + sep + "src" + sep + "pickle";
        Cwd = RepositoryRoot + sep + string.Join(sep, "src", "Pickle.Core", "Prompt");
        GitStatus = new GitStatus(
            Root: RepositoryRoot,
            Branch: "main",
            Upstream: "origin/main",
            Ahead: 1,
            Behind: 0,
            IsDetached: false,
            HeadSha: "3c70545e0d7f4c1a",
            Entries:
            [
                new GitStatusEntry("a.cs", null, GitChangeKind.Modified, GitChangeKind.None),
                new GitStatusEntry("b.cs", null, GitChangeKind.Added, GitChangeKind.None),
                new GitStatusEntry("c.cs", null, GitChangeKind.None, GitChangeKind.Modified),
                new GitStatusEntry("new.txt", null, GitChangeKind.Untracked, GitChangeKind.Untracked),
            ],
            StashCount: 1);

        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["VIRTUAL_ENV"] = RepositoryRoot + sep + ".venv",
        };
        SegmentEnvironment = new SegmentEnvironment
        {
            Home = () => Home,
            FindRepositoryRoot = cwd => RepositoryRoot,
            GetEnvironmentVariable = name => env.GetValueOrDefault(name),
            GetGitStatus = (_, _) => Task.FromResult<GitStatus?>(GitStatus),
            IsNodeProject = _ => true,
            GetNodeVersion = _ => Task.FromResult<string?>("v22.9.0"),
            GetKubeContext = () => new KubeContext("kind-dev", "web"),
            DurationThresholdMs = () => 2000,
        };
        _segments = BuiltInSegments.Create(SegmentEnvironment).ToDictionary(s => s.Type, StringComparer.OrdinalIgnoreCase);
    }

    public string Home { get; }

    public string RepositoryRoot { get; }

    public string Cwd { get; }

    public GitStatus GitStatus { get; }

    public SegmentEnvironment SegmentEnvironment { get; }

    public PromptContext Context(int width, bool lastCommandSucceeded = false) => new(
        Cwd: Cwd,
        LastCommandSucceeded: lastCommandSucceeded,
        LastExitCode: lastCommandSucceeded ? 0 : 1,
        LastCommandDuration: TimeSpan.FromMilliseconds(3250),
        IsAdmin: true,
        JobCount: 1,
        UserName: "pickle",
        HostName: "jar",
        Now: new DateTimeOffset(2026, 9, 23, 14, 5, 9, TimeSpan.Zero),
        TerminalWidth: width);

    public PromptRender Render(Theme theme, int width, bool lastCommandSucceeded = false) =>
        Composer().Compose(Context(width, lastCommandSucceeded), theme, timeoutMs: 1000);

    public string RenderTransient(Theme theme, int width, bool lastCommandSucceeded = true) =>
        Composer().ComposeTransient(Context(width, lastCommandSucceeded), theme);

    /// <summary>Lines of a rendered prompt with the right prompt placed at the end of the input line.</summary>
    public static IReadOnlyList<string> ToLines(PromptRender render, int width)
    {
        var lines = render.Left.Split('\n').ToList();
        if (render.Right is { } right)
        {
            var last = lines[^1];
            var pad = Math.Max(1, width - TextWidth.VisibleWidth(last) - TextWidth.VisibleWidth(right) - 1);
            lines[^1] = last + new string(' ', pad) + right;
        }

        return lines;
    }

    private PromptComposer Composer() => new(type => _segments.GetValueOrDefault(type), new SegmentCache(), CancellationToken.None);
}
