using Pickle.Abstractions;
using Pickle.Core.Prompt;

namespace Pickle.Core.Tests.Prompt;

public class ThemeAnimatorTests
{
    [Fact]
    public void StaticThemesAreReturnedAsIs()
    {
        var theme = Blocks("#FF0000", "#00FF00");
        Assert.False(ThemeAnimator.IsAnimated(theme));
        Assert.Same(theme, ThemeAnimator.Frame(theme, 7));

        theme.Prompt.Animation = new PromptAnimation { Effect = "none" };
        Assert.False(ThemeAnimator.IsAnimated(theme));
    }

    [Fact]
    public void WaveScrollsTheGradientAcrossBlockBackgroundsAndLeavesTheOriginalAlone()
    {
        var theme = Blocks("#101010", "#202020", "#303030");
        theme.Prompt.Animation = new PromptAnimation { Effect = "wave", Colors = ["#FF0000", "#0000FF"], FrameMs = 100, PeriodMs = 1000, Spread = 0.25 };

        var first = ThemeAnimator.Frame(theme, 0);
        Assert.Equal(["#FF0000", "#800080", "#0000FF"], first.Prompt.Left.Select(s => s.Background));
        Assert.All(first.Prompt.Left, s => Assert.Equal("#000000", s.Foreground));
        Assert.Equal("#101010", theme.Prompt.Left[0].Background);

        // Five frames is half the period: every segment has moved half a cycle along the two-stop loop.
        Assert.Equal(["#0000FF", "#800080", "#FF0000"], ThemeAnimator.Frame(theme, 5).Prompt.Left.Select(s => s.Background));
        Assert.Equal(
            first.Prompt.Left.Select(s => s.Background),
            ThemeAnimator.Frame(theme, 10).Prompt.Left.Select(s => s.Background));
    }

    [Fact]
    public void WaveWithoutColorsCyclesTheSegmentsOwnColors()
    {
        var theme = Blocks("#FF0000", "#00FF00", "#0000FF");
        theme.Prompt.Animation = new PromptAnimation { Effect = "wave", FrameMs = 100, PeriodMs = 900, Spread = 0 };

        Assert.All(ThemeAnimator.Frame(theme, 0).Prompt.Left, s => Assert.Equal("#FF0000", s.Background));
        Assert.All(ThemeAnimator.Frame(theme, 3).Prompt.Left, s => Assert.Equal("#00FF00", s.Background));
    }

    [Fact]
    public void RainbowRotatesHueAndKeepsLightness()
    {
        var theme = Blocks("#FF0000");
        theme.Prompt.Animation = new PromptAnimation { Effect = "rainbow", FrameMs = 100, PeriodMs = 1200, PromptChar = false };

        Assert.Equal("#FF0000", ThemeAnimator.Frame(theme, 0).Prompt.Left[0].Background);
        Assert.Equal("#00FF00", ThemeAnimator.Frame(theme, 4).Prompt.Left[0].Background);
        Assert.Equal("#00FFFF", ThemeAnimator.Frame(theme, 6).Prompt.Left[0].Background);
        Assert.Equal("green", ThemeAnimator.Frame(theme, 6).Prompt.PromptCharColor);
    }

    [Fact]
    public void PulseBreathesTowardTheTargetAndBack()
    {
        var theme = Blocks("#000000");
        theme.Prompt.Animation = new PromptAnimation { Effect = "pulse", Colors = ["#FFFFFF"], Intensity = 1, FrameMs = 100, PeriodMs = 1000 };

        Assert.Equal("#000000", ThemeAnimator.Frame(theme, 0).Prompt.Left[0].Background);
        Assert.Equal("#FFFFFF", ThemeAnimator.Frame(theme, 5).Prompt.Left[0].Background);
        Assert.Equal("#000000", ThemeAnimator.Frame(theme, 10).Prompt.Left[0].Background);
    }

    [Fact]
    public void ShimmerLightsOneEndThenTheOther()
    {
        var theme = Blocks("#000000", "#000000", "#000000", "#000000");
        theme.Prompt.Animation = new PromptAnimation { Effect = "shimmer", Intensity = 1, FrameMs = 100, PeriodMs = 1000, PromptChar = false };

        static int Brightest(Theme frame) =>
            frame.Prompt.Left.Select((s, i) => (i, PickleColor.Parse(s.Background)!.Value.R)).MaxBy(x => x.R).i;

        var early = ThemeAnimator.Frame(theme, 2);
        var late = ThemeAnimator.Frame(theme, 7);
        Assert.True(Brightest(early) < Brightest(late), $"{Brightest(early)} then {Brightest(late)}");
        Assert.All(ThemeAnimator.Frame(theme, 0).Prompt.Left, s => Assert.Equal("#000000", s.Background));
    }

    [Fact]
    public void TextSegmentsAnimateTheirForegroundAndNamedColorsUseThePalette()
    {
        var theme = new Theme { Name = "t" };
        theme.Terminal.Red = "#FF0000";
        theme.Prompt.Left = [new SegmentStyle { Type = "text", Foreground = "red" }];
        theme.Prompt.PromptCharColor = "red";
        theme.Prompt.PromptCharErrorColor = "brightRed";
        theme.Prompt.Animation = new PromptAnimation { Effect = "rainbow", FrameMs = 100, PeriodMs = 300, Spread = 0 };

        var frame = ThemeAnimator.Frame(theme, 1);
        Assert.Equal("#00FF00", frame.Prompt.Left[0].Foreground);
        Assert.Null(frame.Prompt.Left[0].Background);
        Assert.Equal("#00FF00", frame.Prompt.PromptCharColor);
        Assert.Equal("brightRed", frame.Prompt.PromptCharErrorColor);
    }

    [Fact]
    public void StatusAdminAndOptedOutSegmentsKeepTheirColors()
    {
        var theme = Blocks("#101010", "#202020", "#303030", "#404040");
        theme.Prompt.Left[0].Type = "admin";
        theme.Prompt.Left[1].Type = "status";
        theme.Prompt.Left[2].Options["animate"] = "false";
        theme.Prompt.Animation = new PromptAnimation { Effect = "wave", Colors = ["#FF0000", "#0000FF"], FrameMs = 100, PeriodMs = 1000 };

        var frame = ThemeAnimator.Frame(theme, 3);
        Assert.Equal(["#101010", "#202020", "#303030"], frame.Prompt.Left.Take(3).Select(s => s.Background));
        Assert.NotEqual("#404040", frame.Prompt.Left[3].Background);
    }

    [Fact]
    public void PromptCharFramesCycleAndArePaddedToTheSameWidth()
    {
        var theme = Blocks("#000000");
        theme.Prompt.Animation = new PromptAnimation { Effect = "none", PromptChars = ["❯", "❯❯", "❯❯❯"], PromptCharFrames = 2 };

        Assert.True(ThemeAnimator.IsAnimated(theme));
        Assert.Equal(["❯  ", "❯  ", "❯❯ ", "❯❯ ", "❯❯❯", "❯❯❯", "❯  "], Enumerable.Range(0, 7).Select(f => ThemeAnimator.Frame(theme, f).Prompt.PromptChar));
        Assert.Equal("#000000", ThemeAnimator.Frame(theme, 3).Prompt.Left[0].Background);
    }

    [Fact]
    public void FramesComeFromElapsedTimeAndTheFrameLengthIsClamped()
    {
        var animation = new PromptAnimation { FrameMs = 5 };
        Assert.Equal(40, ThemeAnimator.FrameMs(animation));
        Assert.Equal(2, ThemeAnimator.FrameAt(animation, 99));
        Assert.Equal(0, ThemeAnimator.FrameAt(animation, -10));
    }

    [Fact]
    public void BuiltInAnimatedThemesMoveAndStayReadable()
    {
        using var t = Pickle.Testing.TestPickle.Create();
        var animated = t.Runtime.ThemeProvider.Available
            .Select(name => t.Runtime.ThemeProvider.Load(name)!)
            .Where(ThemeAnimator.IsAnimated)
            .ToList();
        Assert.NotEmpty(animated);
        foreach (var theme in animated)
        {
            var frameMs = ThemeAnimator.FrameMs(theme.Prompt.Animation!);
            var frames = Enumerable.Range(0, 12).Select(i => ThemeAnimator.Frame(theme, i * 250L / frameMs)).ToList();
            Assert.True(
                frames.Select(Signature).Distinct().Count() > 1,
                $"{theme.Name} doesn't change over three seconds");
            Assert.All(frames, frame => Assert.All(frame.Prompt.Left.Concat(frame.Prompt.Right), s =>
            {
                Assert.True(s.Foreground is null || PickleColor.Parse(s.Foreground) is not null, $"{theme.Name}: bad color {s.Foreground}");
                Assert.True(s.Background is null || PickleColor.Parse(s.Background) is not null, $"{theme.Name}: bad color {s.Background}");
            }));
        }

        static string Signature(Theme frame) =>
            string.Join('|', frame.Prompt.Left.Concat(frame.Prompt.Right).Select(s => s.Foreground + "/" + s.Background))
            + frame.Prompt.PromptChar + frame.Prompt.PromptCharColor;
    }

    private static Theme Blocks(params string[] backgrounds)
    {
        var theme = new Theme { Name = "blocks" };
        theme.Prompt.Left = [.. backgrounds.Select(b => new SegmentStyle { Type = "text", Foreground = "#000000", Background = b })];
        return theme;
    }
}
