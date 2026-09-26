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
    public void ElevatedSessionsGetTheRedLogoWithAnAdminBadge()
    {
        using var t = TestPickle.Create(width: 100, height: 24, configure: c => c.Shell.BannerStyle = "art", start: true, elevated: true);

        StartupBanner.Write(t.Runtime, _ => { });

        var screen = t.Terminal.GetScreenText();
        Assert.Contains("│  ADMIN  │", screen, StringComparison.Ordinal);
        Assert.Contains("⚡ Administrator", screen, StringComparison.Ordinal);
        var red = PickleColor.Parse(t.Runtime.Themes.Current.Terminal.Red)!.Value;
        Assert.Contains($"«fg=#{red.R:X2}{red.G:X2}{red.B:X2}", t.Terminal.GetStyledScreen(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("admin", t.Runtime.Themes.Current.Name);
    }

    [Fact]
    public void NormalSessionsHaveNoBadge()
    {
        using var t = TestPickle.Create(width: 100, height: 24, configure: c => c.Shell.BannerStyle = "art", start: true);

        StartupBanner.Write(t.Runtime, _ => { });

        Assert.DoesNotContain("ADMIN", t.Terminal.GetScreenText(), StringComparison.Ordinal);
        Assert.Equal("pickle", t.Runtime.Themes.Current.Name);
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
    public void FirstStartWelcomesAndUpgradesOnlyAskNewOffers()
    {
        using var t = TestPickle.Create(start: true);
        var offers = t.Runtime.Services.Require<IFirstRunOffers>();
        offers.Add(new FirstRunOffer("old", "Old question?", () => true, () => "old done"));
        offers.Add(new FirstRunOffer("new", "New question?", () => true, () => "new done") { Since = FirstRun.SetupVersion, Progress = "Working…" });
        t.Runtime.Config.Update(c => (c.Shell.FirstRunCompleted, c.Shell.SetupVersion) = (true, 0));

        t.Terminal.Press("Enter");
        t.Runtime.FirstRun.Run(t.Runtime);

        var screen = t.Terminal.GetScreenText();
        Assert.DoesNotContain("Welcome to Pickle", screen, StringComparison.Ordinal);
        Assert.DoesNotContain("Old question?", screen, StringComparison.Ordinal);
        Assert.Contains("New in this version of Pickle:", screen, StringComparison.Ordinal);
        Assert.Contains("New question? [Y/n] yes", screen, StringComparison.Ordinal);
        Assert.Contains("Working…", screen, StringComparison.Ordinal);
        Assert.Contains("pk setup", screen, StringComparison.Ordinal);
        Assert.Equal(FirstRun.SetupVersion, t.Runtime.Config.Current.Shell.SetupVersion);
    }

    [Fact]
    public void AFreshStartShowsTheWelcomeEvenWithNothingToAsk()
    {
        using var t = TestPickle.Create(width: 120, start: true);
        t.Runtime.FirstRun.Run(t.Runtime);

        var screen = t.Terminal.GetScreenText();
        Assert.Contains("Welcome to Pickle!", screen, StringComparison.Ordinal);
        Assert.Contains("pk help", screen, StringComparison.Ordinal);
        Assert.True(t.Runtime.Config.Current.Shell.FirstRunCompleted);
    }

    [Fact]
    public async Task PkSetupAsksEveryRelevantOfferAgain()
    {
        using var t = TestPickle.Create(start: true);
        var accepted = 0;
        t.Runtime.Services.Require<IFirstRunOffers>().Add(new FirstRunOffer("a", "Do A?", () => true, () => $"did A {++accepted}"));
        t.Terminal.Press("Enter", "Enter");
        t.Runtime.FirstRun.Run(t.Runtime);
        t.Runtime.FirstRun.Run(t.Runtime);
        Assert.Equal(1, accepted);

        var output = new List<string>();
        var context = new PickleCommandContext
        {
            Pickle = t.Runtime,
            WriteObject = _ => { },
            WriteHost = output.Add,
            WriteError = output.Add,
            Confirm = (_, d) => d,
            Cwd = t.Home,
        };
        Assert.Equal(0, await t.Runtime.CommandRegistry.Get("setup")!.ExecuteAsync(context, [], CancellationToken.None));

        Assert.Equal(2, accepted);
        Assert.Empty(output);
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
