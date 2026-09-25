using Pickle.Abstractions;
using Pickle.Core.Hosting;
using Pickle.Testing;

namespace Pickle.Core.Tests.Hosting;

public class StartupTests
{
    [Fact]
    public void AnimatedBannerDrawsTheArtAndSweepsOnce()
    {
        using var t = TestPickle.Create(width: 80, height: 24, start: true);
        var sleeps = 0;
        StartupBanner.Write(t.Runtime, _ => sleeps++);
        var screen = t.Terminal.GetScreenText();
        Assert.Contains("██████╗ ██╗ ██████╗██╗  ██╗██╗     ███████╗", screen, StringComparison.Ordinal);
        Assert.Contains("🥒 Pickle " + PickleRuntime.Version, screen, StringComparison.Ordinal);
        Assert.True(sleeps > 5, $"{sleeps} frames");
        var raw = t.Terminal.RawOutput;
        Assert.True(raw.LastIndexOf(Ansi.ShowCursor, StringComparison.Ordinal) > raw.LastIndexOf(Ansi.HideCursor, StringComparison.Ordinal));
    }

    [Fact]
    public void AKeyEndsTheAnimationAndIsLeftForTheEditor()
    {
        using var t = TestPickle.Create(width: 80, height: 24, start: true);
        t.Terminal.Paste("l");
        var sleeps = 0;
        StartupBanner.Write(t.Runtime, _ => sleeps++);
        Assert.Equal(1, sleeps);
        Assert.Equal(1, t.Terminal.PendingKeyCount);
        Assert.Contains("╚═╝     ╚═╝ ╚═════╝╚═╝  ╚═╝╚══════╝╚══════╝", t.Terminal.GetScreenText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("art", 80)]
    [InlineData("line", 80)]
    [InlineData("animated", 40)]
    public void StaticArtLineStyleAndNarrowTerminals(string style, int width)
    {
        using var t = TestPickle.Create(width: width, height: 24, configure: c => c.Shell.BannerStyle = style, start: true);
        var sleeps = 0;
        StartupBanner.Write(t.Runtime, _ => sleeps++);
        Assert.Equal(0, sleeps);
        var lines = t.Terminal.GetScreenText().Split('\n').Where(l => l.Trim().Length > 0).ToList();
        Assert.Equal(style == "art" ? StartupBanner.Art.Length + 1 : 1, lines.Count);
        Assert.StartsWith("🥒 Pickle", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void FirstRunAsksRelevantOffersOnceAndRemembers()
    {
        using var t = TestPickle.Create(start: true);
        var accepted = new List<string>();
        var offers = t.Runtime.Services.Require<IFirstRunOffers>();
        offers.Add(new FirstRunOffer("a", "Do A?", () => true, () => { accepted.Add("a"); return "did A"; }));
        offers.Add(new FirstRunOffer("b", "Do B?", () => true, () => { accepted.Add("b"); return "did B"; }));
        offers.Add(new FirstRunOffer("c", "Do C?", () => false, () => { accepted.Add("c"); return "did C"; }));

        t.Terminal.Type("y").Type("n");
        t.Runtime.FirstRun.Run(t.Runtime);

        Assert.Equal(["a"], accepted);
        var screen = t.Terminal.GetScreenText();
        Assert.Contains("Do A? [Y/n] yes", screen, StringComparison.Ordinal);
        Assert.Contains("did A", screen, StringComparison.Ordinal);
        Assert.Contains("Do B? [Y/n] no", screen, StringComparison.Ordinal);
        Assert.DoesNotContain("Do C?", screen, StringComparison.Ordinal);
        Assert.True(t.Runtime.Config.Current.Shell.FirstRunCompleted);

        t.Runtime.FirstRun.Run(t.Runtime);
        Assert.Equal(["a"], accepted);
    }

    [Fact]
    public void AFailingOfferIsReportedAndTheRestStillRun()
    {
        using var t = TestPickle.Create(start: true);
        var offers = t.Runtime.Services.Require<IFirstRunOffers>();
        offers.Add(new FirstRunOffer("boom", "Break?", () => true, () => throw new IOException("disk full")));
        offers.Add(new FirstRunOffer("ok", "Fine?", () => true, () => "fine"));
        t.Terminal.Press("Enter", "Enter");
        t.Runtime.FirstRun.Run(t.Runtime);
        var screen = t.Terminal.GetScreenText();
        Assert.Contains("disk full", screen, StringComparison.Ordinal);
        Assert.Contains("fine", screen, StringComparison.Ordinal);
    }
}
