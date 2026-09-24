using System.Diagnostics;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Core.Prompt;
using Pickle.Testing;
using Pickle.Testing.Fakes;

namespace Pickle.Core.Tests.Prompt;

public class PromptEngineTests
{
    private const int TimeoutMs = 300;

    [Fact]
    public void SlowSegmentOnColdCacheWaitsAtMostTheTimeoutThenShowsWithoutIt()
    {
        using var t = Create(out var engine);
        var gate = new TaskCompletionSource<PromptSegmentOutput?>(TaskCreationOptions.RunContinuationsAsynchronously);
        t.Runtime.PromptSegmentRegistry.Register(new DelegateSegment("slow", () => new ValueTask<PromptSegmentOutput?>(gate.Task)));
        var theme = Theme("slow");

        var cold = Time(() => engine.Render(Context(), theme), out var elapsed);
        Assert.InRange(elapsed, TimeoutMs - 50, TimeoutMs + 2000);
        Assert.Equal("fast ❯ ", Plain(cold.Left));

        gate.SetResult(new PromptSegmentOutput("SLOW"));
        var warm = Time(() => engine.Render(Context(), theme), out elapsed);
        Assert.True(elapsed < TimeoutMs - 50, $"warm render took {elapsed} ms");
        Assert.Equal("SLOW fast ❯ ", Plain(warm.Left));
    }

    [Fact]
    public async Task RefreshesAfterEachCommandAndCachesInBetween()
    {
        using var t = Create(out var engine);
        var calls = 0;
        t.Runtime.PromptSegmentRegistry.Register(new DelegateSegment("counter", async () =>
        {
            await Task.Delay(10);
            return new PromptSegmentOutput("v" + Interlocked.Increment(ref calls));
        }));
        var theme = Theme("counter");

        Assert.Equal("v1 fast ❯ ", Plain(engine.Render(Context(), theme).Left));
        Assert.Equal("v1 fast ❯ ", Plain(engine.Render(Context(), theme).Left));
        Assert.Equal(1, calls);

        await t.Runtime.Hooks.RaiseAsync(new HookEvent(HookKind.PostExecute, "git add ."));
        Assert.Equal("v2 fast ❯ ", Plain(engine.Render(Context(), theme).Left));

        // Separate cache entry per directory.
        Assert.Equal("v3 fast ❯ ", Plain(engine.Render(Context("/elsewhere"), theme).Left));
        Assert.Equal("v2 fast ❯ ", Plain(engine.Render(Context(), theme).Left));
    }

    [Fact]
    public void KnownSlowSegmentShowsStaleValueImmediatelyAndSignalsTheLateResult()
    {
        using var t = Create(out var engine);
        var gate = new TaskCompletionSource<PromptSegmentOutput?>(TaskCreationOptions.RunContinuationsAsynchronously);
        t.Runtime.PromptSegmentRegistry.Register(new DelegateSegment("slow", () => new ValueTask<PromptSegmentOutput?>(gate.Task)));
        var theme = Theme("slow");
        using var refreshed = new ManualResetEventSlim();
        engine.SegmentsRefreshed += (_, _) => refreshed.Set();

        engine.Render(Context(), theme);
        gate.SetResult(new PromptSegmentOutput("A"));
        Assert.True(refreshed.Wait(5000), "late result was not signalled");
        Assert.Equal("A fast ❯ ", Plain(engine.Render(Context(), theme).Left));

        refreshed.Reset();
        gate = new TaskCompletionSource<PromptSegmentOutput?>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.InvalidateSegments();
        var stale = Time(() => engine.Render(Context(), theme), out var elapsed);
        Assert.Equal("A fast ❯ ", Plain(stale.Left));
        Assert.True(elapsed < TimeoutMs - 50, $"stale render took {elapsed} ms");

        gate.SetResult(new PromptSegmentOutput("B"));
        Assert.True(refreshed.Wait(5000), "late result was not signalled");
        Assert.Equal("B fast ❯ ", Plain(engine.Render(Context(), theme).Left));
    }

    [Fact]
    public void PrefetchStartsTheRefreshAheadOfRender()
    {
        using var t = Create(out var engine);
        var started = 0;
        var gate = new TaskCompletionSource<PromptSegmentOutput?>(TaskCreationOptions.RunContinuationsAsynchronously);
        t.Runtime.PromptSegmentRegistry.Register(new DelegateSegment("slow", () =>
        {
            Interlocked.Increment(ref started);
            return new ValueTask<PromptSegmentOutput?>(gate.Task);
        }));
        t.Runtime.ThemeProvider.Current.Prompt.Left = [new SegmentStyle { Type = "slow" }];

        engine.Prefetch(Context());
        Assert.Equal(1, started);
        gate.SetResult(new PromptSegmentOutput("ready"));
        Assert.Contains("ready", Plain(engine.Render(Context()).Left), StringComparison.Ordinal);
        Assert.Equal(1, started);
    }

    [Fact]
    public void FailingSegmentsAreHidden()
    {
        using var t = Create(out var engine);
        t.Runtime.PromptSegmentRegistry.Register(new DelegateSegment("boom", () => throw new InvalidOperationException("boom")));
        t.Runtime.PromptSegmentRegistry.Register(new DelegateSegment("boomAsync", async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("boom");
        }));
        Assert.Equal("fast ❯ ", Plain(engine.Render(Context(), Theme("boom", "boomAsync", "missing")).Left));
    }

    [Fact]
    public void GitSegmentReadsTheRegisteredGitService()
    {
        using var t = Create(out var engine);
        var git = new FakeGitService
        {
            Status = new GitStatus("/r", "main", "origin/main", 1, 0, false, "abc", [new("x.cs", null, GitChangeKind.None, GitChangeKind.Modified)], 0),
        };
        t.Runtime.ServiceRegistry.Add<IGitService>(git);

        var left = Plain(engine.Render(t.Runtime.CreatePromptContext()).Left);
        Assert.Contains(" main ↑1 !1", left, StringComparison.Ordinal);

        git.Status = null;
        engine.InvalidateSegments();
        Assert.DoesNotContain("main", Plain(engine.Render(t.Runtime.CreatePromptContext()).Left), StringComparison.Ordinal);
    }

    [Fact]
    public void NewlineBeforePromptSkipsTheFirstPrompt()
    {
        using var t = TestPickle.Create(configure: c => c.Prompt.NewlineBeforePrompt = true);
        var engine = (PromptEngine)t.Runtime.Prompt;
        engine.Initialize();
        Assert.False(engine.Render(Context()).Left.StartsWith('\n'));
        Assert.StartsWith("\n", engine.Render(Context()).Left, StringComparison.Ordinal);
    }

    [Fact]
    public void RendersTheCurrentThemeAndColorsThePromptCharByStatus()
    {
        using var t = Create(out var engine);
        var theme = t.Runtime.Themes.Current;
        var ok = engine.Render(Context());
        var failed = engine.Render(Context() with { LastCommandSucceeded = false, LastExitCode = 2 });
        Assert.Contains(Ansi.Colorize("❯", theme.Prompt.PromptCharColor), ok.Left, StringComparison.Ordinal);
        Assert.Contains(Ansi.Colorize("❯", theme.Prompt.PromptCharErrorColor), failed.Left, StringComparison.Ordinal);
        Assert.Contains("✘ 2", Plain(failed.Right ?? string.Empty), StringComparison.Ordinal);
        Assert.Equal("∙ ", Plain(ok.Continuation));
        Assert.Equal("❯ ", Plain(engine.RenderTransient(Context())));
    }

    [Fact]
    public void BuiltInSegmentsAreRegisteredForEveryThemeSegmentType()
    {
        using var t = Create(out _);
        foreach (var name in t.Runtime.ThemeProvider.Available)
        {
            var theme = t.Runtime.ThemeProvider.Load(name)!;
            foreach (var style in theme.Prompt.Left.Concat(theme.Prompt.Right))
            {
                Assert.True(t.Runtime.PromptSegmentRegistry.Get(style.Type) is not null, $"{name}: segment '{style.Type}' is not registered");
            }
        }
    }

    private static TestPickle Create(out PromptEngine engine)
    {
        var t = TestPickle.Create(configure: c => c.Prompt.GitTimeoutMs = TimeoutMs);
        engine = (PromptEngine)t.Runtime.Prompt;
        engine.Initialize();
        return t;
    }

    private static Theme Theme(params string[] types)
    {
        var theme = new Theme { Name = "test" };
        theme.Prompt.Left = [.. types.Select(type => new SegmentStyle { Type = type })];
        theme.Prompt.Left.Add(new SegmentStyle { Type = "text", Options = { ["text"] = "fast" } });
        return theme;
    }

    private static PromptContext Context(string cwd = "/work/repo") =>
        new(cwd, true, 0, null, false, 0, "me", "box", new DateTimeOffset(2026, 9, 23, 14, 5, 9, TimeSpan.Zero), 80);

    private static string Plain(string text) => TextWidth.StripAnsi(text);

    private static T Time<T>(Func<T> action, out long elapsedMs)
    {
        var sw = Stopwatch.StartNew();
        var result = action();
        elapsedMs = sw.ElapsedMilliseconds;
        return result;
    }

    private sealed class DelegateSegment(string type, Func<ValueTask<PromptSegmentOutput?>> render) : IPromptSegment
    {
        public string Type => type;

        public ValueTask<PromptSegmentOutput?> RenderAsync(PromptContext context, SegmentStyle style, CancellationToken cancellationToken) => render();
    }
}
