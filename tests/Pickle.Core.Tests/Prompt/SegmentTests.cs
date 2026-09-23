using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Core.Prompt;
using Pickle.Core.Prompt.Segments;
using Pickle.Testing.Fakes;

namespace Pickle.Core.Tests.Prompt;

public class SegmentTests
{
    private const string Bold = "\u001b[1m";
    private const string Unbold = "\u001b[22m";

    [Theory]
    [InlineData("/home/pickle", "~")]
    [InlineData("/home/pickle/src/app", "~/src/app")]
    [InlineData("/home/pickle2/x", "/home/pickle2/x")]
    [InlineData("/usr/local/bin", "/usr/local/bin")]
    [InlineData("/", "/")]
    public void CwdReplacesHomeWithTilde(string cwd, string expected) =>
        Assert.Equal(expected, CwdSegment.Format(cwd, "/home/pickle"));

    [Theory]
    [InlineData(@"C:\Users\Pickle\source\repos", @"~\source\repos")]
    [InlineData(@"c:\users\pickle\Documents", @"~\Documents")]
    [InlineData(@"C:\Users\Pickle", "~")]
    [InlineData(@"D:\work\app\", @"D:\work\app")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"\\server\share\team\docs", @"\\server\share\team\docs")]
    [InlineData("C:/Users/Pickle/tools", @"~\tools")]
    public void CwdFormatsWindowsPaths(string cwd, string expected) =>
        Assert.Equal(expected, CwdSegment.Format(cwd, @"C:\Users\Pickle"));

    [Theory]
    [InlineData(0, "~/a/b/c/d/e")]
    [InlineData(5, "~/a/b/c/d/e")]
    [InlineData(3, "~/a/…/d/e")]
    [InlineData(2, "~/a/…/e")]
    [InlineData(1, "~/…/e")]
    public void CwdMaxDepthCollapsesTheMiddle(int maxDepth, string expected) =>
        Assert.Equal(expected, CwdSegment.Format("/home/me/a/b/c/d/e", "/home/me", maxDepth));

    [Fact]
    public void CwdMaxDepthCollapsesWindowsPaths() =>
        Assert.Equal(@"D:\src\…\Pickle.Core\Prompt", CwdSegment.Format(@"D:\src\pickle\src\Pickle.Core\Prompt", null, 3));

    [Fact]
    public void CwdBoldsRepositoryRootAndKeepsItWhenCollapsing()
    {
        Assert.Equal($"~/src/{Bold}pickle{Unbold}/docs", CwdSegment.Format("/home/me/src/pickle/docs", "/home/me", 0, "/home/me/src/pickle"));
        Assert.Equal(
            $"~/src/…/{Bold}pickle{Unbold}/…/Prompt",
            CwdSegment.Format("/home/me/src/github/pickle/src/Core/Prompt", "/home/me", 3, "/home/me/src/github/pickle"));
        Assert.Equal(
            $@"~\{Bold}Pickle{Unbold}\src",
            CwdSegment.Format(@"C:\Users\Me\pickle\src", @"C:\Users\Me", 0, @"c:\users\me\PICKLE"));
    }

    [Fact]
    public void CwdIgnoresRepositoryOutsideThePath() =>
        Assert.Equal("~/a", CwdSegment.Format("/home/me/a", "/home/me", 0, "/srv/repo"));

    [Fact]
    public void CwdStripsControlCharactersFromDirectoryNames() =>
        Assert.Equal("/tmp/evil?[31mdir", CwdSegment.Format("/tmp/evil\u001b[31mdir", null));

    [Fact]
    public void CwdSegmentHonoursOptions()
    {
        var env = Environment(home: "/home/me", repoRoot: "/home/me/r");
        var segment = new CwdSegment(env);
        var noTilde = new SegmentStyle { Type = "cwd", Options = { ["tildeHome"] = "false", ["boldRepoRoot"] = "false" } };
        Assert.Equal("/home/me/r/x", segment.Render("/home/me/r/x", noTilde));
        Assert.Equal($"~/{Bold}r{Unbold}/x", segment.Render("/home/me/r/x", new SegmentStyle { Type = "cwd" }));
    }

    [Fact]
    public void GitFormatsBranchCountsAndOperation()
    {
        var status = new GitStatus(
            "/r", "feature/x", "origin/feature/x", Ahead: 2, Behind: 1, IsDetached: false, HeadSha: "abcdef1234", Entries:
            [
                new("staged.cs", null, GitChangeKind.Added, GitChangeKind.None),
                new("both.cs", null, GitChangeKind.Modified, GitChangeKind.Modified),
                new("dirty.cs", null, GitChangeKind.None, GitChangeKind.Modified),
                new("new.txt", null, GitChangeKind.Untracked, GitChangeKind.Untracked),
                new("conflict.cs", null, GitChangeKind.Unmerged, GitChangeKind.Unmerged),
            ],
            StashCount: 3,
            Operation: "REBASING");

        Assert.Equal("feature/x REBASING ↑2 ↓1 +3 !3 ?1 ✖1 ≡3", GitSegment.Format(status));
        Assert.Equal("feature/x REBASING", GitSegment.Format(status, counts: false));
    }

    [Fact]
    public void GitShowsShortShaWhenDetachedAndNothingWhenClean()
    {
        var detached = new GitStatus("/r", null, null, 0, 0, IsDetached: true, HeadSha: "3c70545e0d7f", Entries: [], StashCount: 0);
        Assert.Equal("3c70545", GitSegment.Format(detached));
        Assert.Equal("main", GitSegment.Format(detached with { IsDetached = false, Branch = "main" }));
    }

    [Fact]
    public async Task GitSegmentUsesTheGitServiceAndHidesOutsideRepositories()
    {
        var git = new FakeGitService();
        var env = SegmentEnvironment.Create(() => git, () => 2000);
        var segment = new GitSegment(env);
        var style = new SegmentStyle { Type = "git" };

        Assert.Null(await segment.RenderAsync(Context("/tmp"), style, CancellationToken.None));

        git.Status = new GitStatus("/r", "main", "origin/main", 0, 0, false, "abc", [], 0);
        var output = await segment.RenderAsync(Context("/r"), style, CancellationToken.None);
        Assert.Equal("main", output?.Text);
    }

    [Theory]
    [InlineData(350, "350ms")]
    [InlineData(2300, "2.3s")]
    [InlineData(59_960, "59.9s")]
    [InlineData(64_000, "1m 04s")]
    [InlineData(3_599_000, "59m 59s")]
    [InlineData(3_720_000, "1h 02m")]
    [InlineData(93_600_000, "1d 02h")]
    public void DurationHumanizes(long ms, string expected) =>
        Assert.Equal(expected, DurationSegment.Humanize(TimeSpan.FromMilliseconds(ms)));

    [Fact]
    public async Task DurationRespectsThresholdAndOptionOverride()
    {
        var segment = new DurationSegment(Environment(threshold: 2000));
        var style = new SegmentStyle { Type = "duration" };
        Assert.Null(await segment.RenderAsync(Context("/", duration: TimeSpan.FromSeconds(1.5)), style, CancellationToken.None));
        Assert.Equal("2.0s", (await segment.RenderAsync(Context("/", duration: TimeSpan.FromSeconds(2)), style, CancellationToken.None))?.Text);

        style.Options["thresholdMs"] = "100";
        Assert.Equal("150ms", (await segment.RenderAsync(Context("/", duration: TimeSpan.FromMilliseconds(150)), style, CancellationToken.None))?.Text);
    }

    [Fact]
    public void VenvPrefersPromptNameThenProjectFolder()
    {
        Assert.Equal("api", VenvSegment.Resolve(Env(("VIRTUAL_ENV", "/src/api/.venv"))));
        Assert.Equal("tools", VenvSegment.Resolve(Env(("VIRTUAL_ENV", @"C:\src\tools\venv\"))));
        Assert.Equal("myenv", VenvSegment.Resolve(Env(("VIRTUAL_ENV", "/envs/myenv"))));
        Assert.Equal("custom", VenvSegment.Resolve(Env(("VIRTUAL_ENV", "/src/api/.venv"), ("VIRTUAL_ENV_PROMPT", "(custom) "))));
        Assert.Equal("ml", VenvSegment.Resolve(Env(("CONDA_DEFAULT_ENV", "ml"))));
        Assert.Null(VenvSegment.Resolve(Env(("CONDA_DEFAULT_ENV", "base"))));
        Assert.Equal("base", VenvSegment.Resolve(Env(("CONDA_DEFAULT_ENV", "base")), showCondaBase: true));
        Assert.Null(VenvSegment.Resolve(Env()));
    }

    [Fact]
    public void KubeConfigParsesCurrentContextAndNamespaces()
    {
        var yaml = """
            apiVersion: v1
            clusters:
            - cluster:
                server: https://127.0.0.1:6443
              name: kind-dev
            contexts:
            - context:
                cluster: kind-dev
                namespace: web   # team namespace
                user: kind-dev
              name: kind-dev
            - name: "prod"
              context:
                cluster: prod
                user: admin
            current-context: "kind-dev"
            kind: Config
            users:
            - name: kind-dev
              user: {}
            """;
        var info = KubeConfigInfo.Parse(yaml);
        Assert.Equal("kind-dev", info.CurrentContext);
        Assert.Equal("web", info.Namespaces["kind-dev"]);
        Assert.False(info.Namespaces.ContainsKey("prod"));

        Assert.Null(KubeConfigInfo.Parse("apiVersion: v1\ncurrent-context: \"\"\n").CurrentContext);
    }

    [Fact]
    public void KubeConfigReaderMergesKubeconfigListAndReloadsOnChange()
    {
        var dir = Directory.CreateTempSubdirectory("pickle-kube").FullName;
        try
        {
            var first = Path.Combine(dir, "a.yaml");
            var second = Path.Combine(dir, "b.yaml");
            File.WriteAllText(first, "contexts:\n- name: staging\n  context:\n    namespace: api\n");
            File.WriteAllText(second, "current-context: staging\n");
            var env = new Dictionary<string, string> { ["KUBECONFIG"] = first + Path.PathSeparator + second };
            var reader = new KubeConfigReader(env.GetValueOrDefault, () => dir);

            Assert.Equal(new KubeContext("staging", "api"), reader.Read());

            File.WriteAllText(second, "current-context: production-cluster\n");
            File.SetLastWriteTimeUtc(second, DateTime.UtcNow.AddMinutes(1));
            Assert.Equal(new KubeContext("production-cluster", null), reader.Read());

            Assert.Null(new KubeConfigReader(_ => null, () => dir).Read());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task K8sSegmentAppendsNonDefaultNamespace()
    {
        var kube = new KubeContext("kind-dev", "web");
        var segment = new K8sSegment(Environment(kube: () => kube));
        var style = new SegmentStyle { Type = "k8s" };
        Assert.Equal("kind-dev:web", (await segment.RenderAsync(Context("/"), style, CancellationToken.None))?.Text);

        kube = new KubeContext("kind-dev", "default");
        Assert.Equal("kind-dev", (await segment.RenderAsync(Context("/"), style, CancellationToken.None))?.Text);

        kube = null!;
        Assert.Null(await segment.RenderAsync(Context("/"), style, CancellationToken.None));
    }

    [Fact]
    public async Task NodeShowsVersionOnlyInProjects()
    {
        var calls = 0;
        var env = Environment(isNodeProject: dir => dir.StartsWith("/web", StringComparison.Ordinal), nodeVersion: () =>
        {
            calls++;
            return "v22.9.0";
        });
        var segment = new NodeSegment(env);
        var style = new SegmentStyle { Type = "node" };
        Assert.Null(await segment.RenderAsync(Context("/tmp"), style, CancellationToken.None));
        Assert.Equal("v22.9.0", (await segment.RenderAsync(Context("/web/app"), style, CancellationToken.None))?.Text);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("v22.9.0\n", "v22.9.0")]
    [InlineData("  v18.20.4  \r\n", "v18.20.4")]
    [InlineData("", null)]
    [InlineData("node: command not found", null)]
    public void NodeVersionParsing(string output, string? expected) => Assert.Equal(expected, NodeVersionProbe.ParseVersion(output));

    [Fact]
    public async Task NodeVersionProbeRunsOnceAndSurvivesMissingExecutable()
    {
        var probe = new NodeVersionProbe("pickle-no-such-node-binary");
        Assert.Null(await probe.GetVersionAsync(CancellationToken.None));
        Assert.Same(probe.GetVersionAsync(CancellationToken.None).IsCompleted ? "done" : "pending", "done");
    }

    [Fact]
    public void NodeProjectDetectionWalksUpToPackageJson()
    {
        var dir = Directory.CreateTempSubdirectory("pickle-node").FullName;
        try
        {
            var nested = Directory.CreateDirectory(Path.Combine(dir, "src", "components")).FullName;
            var env = SegmentEnvironment.Create(() => null, () => 0);
            Assert.False(env.IsNodeProject(nested) && SegmentEnvironment.FindUpwards(nested, "package.json", directory: false) == dir);
            File.WriteAllText(Path.Combine(dir, "package.json"), "{}");
            Assert.True(env.IsNodeProject(nested));
            Assert.Equal(Path.GetFullPath(dir), SegmentEnvironment.FindUpwards(nested, "package.json", directory: false));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void GitRootDetectionFindsDotGitDirectoryOrFile()
    {
        var dir = Directory.CreateTempSubdirectory("pickle-gitroot").FullName;
        try
        {
            var nested = Directory.CreateDirectory(Path.Combine(dir, "repo", "src")).FullName;
            Directory.CreateDirectory(Path.Combine(dir, "repo", ".git"));
            Assert.Equal(Path.Combine(Path.GetFullPath(dir), "repo"), SegmentEnvironment.FindGitRoot(nested));

            var worktree = Directory.CreateDirectory(Path.Combine(dir, "wt", "lib")).FullName;
            File.WriteAllText(Path.Combine(dir, "wt", ".git"), "gitdir: ../repo/.git/worktrees/wt");
            Assert.Equal(Path.Combine(Path.GetFullPath(dir), "wt"), SegmentEnvironment.FindGitRoot(worktree));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ContextSegments()
    {
        var style = new SegmentStyle();
        var failed = Context("/", succeeded: false, exitCode: 127, admin: true, jobs: 2);
        Assert.Equal("✘ 127", (await new StatusSegment().RenderAsync(failed, style, CancellationToken.None))?.Text);
        Assert.Equal("✘", (await new StatusSegment().RenderAsync(failed with { LastExitCode = 0 }, style, CancellationToken.None))?.Text);
        Assert.Null(await new StatusSegment().RenderAsync(Context("/"), style, CancellationToken.None));
        Assert.Equal("⚡", (await new AdminSegment().RenderAsync(failed, style, CancellationToken.None))?.Text);
        Assert.Equal("ADMIN", (await new AdminSegment().RenderAsync(failed, new SegmentStyle { Options = { ["symbol"] = "ADMIN" } }, CancellationToken.None))?.Text);
        Assert.Null(await new AdminSegment().RenderAsync(Context("/"), style, CancellationToken.None));
        Assert.Equal("2", (await new JobsSegment().RenderAsync(failed, style, CancellationToken.None))?.Text);
        Assert.Null(await new JobsSegment().RenderAsync(Context("/"), style, CancellationToken.None));
        Assert.Equal("me", (await new UserSegment().RenderAsync(failed, style, CancellationToken.None))?.Text);
        Assert.Equal("box", (await new HostSegment().RenderAsync(failed, style, CancellationToken.None))?.Text);
        Assert.Equal("14:05:09", (await new TimeSegment().RenderAsync(failed, style, CancellationToken.None))?.Text);
        Assert.Equal("2026-09-23 14h05", TimeSegment.Format(failed.Now, "yyyy-MM-dd HH'h'mm"));
        Assert.Equal(string.Empty, (await new TextSegment().RenderAsync(failed, style, CancellationToken.None))?.Text);
    }

    [Theory]
    [InlineData(null, "main", null, "main")]
    [InlineData(null, "main", "X", "X main")]
    [InlineData(" {icon} {value}", "main", null, " main")]
    [InlineData("{icon} {value}", "main", "", "main")]
    [InlineData("[{value}]", "{icon}", "I", "[{icon}]")]
    [InlineData("PS ", "", null, "PS ")]
    public void TemplatesSubstituteValueAndIcon(string? template, string value, string? icon, string expected) =>
        Assert.Equal(expected, PromptComposer.ApplyTemplate(template, value, icon));

    private static Func<string, string?> Env(params (string Key, string Value)[] vars)
    {
        var map = vars.ToDictionary(v => v.Key, v => v.Value);
        return map.GetValueOrDefault;
    }

    private static PromptContext Context(
        string cwd,
        bool succeeded = true,
        int? exitCode = 0,
        TimeSpan? duration = null,
        bool admin = false,
        int jobs = 0) =>
        new(cwd, succeeded, exitCode, duration, admin, jobs, "me", "box", new DateTimeOffset(2026, 9, 23, 14, 5, 9, TimeSpan.Zero), 80);

    private static SegmentEnvironment Environment(
        string? home = null,
        string? repoRoot = null,
        int threshold = 2000,
        Func<KubeContext?>? kube = null,
        Func<string, bool>? isNodeProject = null,
        Func<string?>? nodeVersion = null)
    {
        Task<string?>? version = null;
        return new SegmentEnvironment
        {
            Home = () => home,
            FindRepositoryRoot = _ => repoRoot,
            GetEnvironmentVariable = _ => null,
            GetGitStatus = (_, _) => Task.FromResult<GitStatus?>(null),
            IsNodeProject = isNodeProject ?? (_ => false),
            GetNodeVersion = _ => version ??= Task.FromResult(nodeVersion?.Invoke()),
            GetKubeContext = kube ?? (() => null),
            DurationThresholdMs = () => threshold,
        };
    }
}
