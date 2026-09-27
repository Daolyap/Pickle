using System.Text.Json;
using Pickle.Abstractions;
using Pickle.Testing;
using Pickle.Windows.Terminal;

namespace Pickle.Windows.Tests.Terminal;

public sealed class WindowsTerminalTests : IDisposable
{
    private const string Exe = @"C:\Program Files\Pickle\pickle.exe";
    private readonly string _root = Directory.CreateTempSubdirectory("pickle-wt").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void ProfileGuidMatchesTerminalsFragmentAlgorithm()
    {
        // The sanity value from the Windows Terminal fragment documentation.
        Assert.Equal(new Guid("2ece5bfe-50ed-5f3a-ab87-5cd4baafed2b"), WindowsTerminalFragment.GenerateProfileGuid("Git", "Git Bash"));
        Assert.Equal("{b502e694-4c43-5dac-8920-383ec6adaf5e}", WindowsTerminalFragment.ProfileGuidString);
    }

    [Fact]
    public void FragmentJson()
    {
        var settings = new TerminalSettings { BackgroundImage = @"C:\Users\me\Pictures\jar.png", BackgroundImageOpacity = 0.25 };
        var json = WindowsTerminalFragment.Build(Exe, settings, new TerminalPalette());
        Snapshot.Match(json);

        using var doc = JsonDocument.Parse(json);
        var profile = doc.RootElement.GetProperty("profiles")[0];
        Assert.Equal("\"" + Exe + "\"", profile.GetProperty("commandline").GetString());
        Assert.Equal(WindowsTerminalFragment.SchemeName, profile.GetProperty("colorScheme").GetString());
        var scheme = doc.RootElement.GetProperty("schemes")[0];
        Assert.Equal(21, scheme.EnumerateObject().Count());
    }

    [Fact]
    public void SchemeResolvesNamedColorsAndOmitsUnsetAppearance()
    {
        var palette = new TerminalPalette { Background = "black", Foreground = "not-a-color", Red = "#abc" };
        var settings = new TerminalSettings { FontFace = null, FontSize = null, Opacity = null, Padding = null };
        using var doc = JsonDocument.Parse(WindowsTerminalFragment.Build(Exe, settings, palette));
        var scheme = doc.RootElement.GetProperty("schemes")[0];
        Assert.Equal("#1B1F1A", scheme.GetProperty("background").GetString());
        Assert.Equal(new TerminalPalette().Foreground, scheme.GetProperty("foreground").GetString());
        Assert.Equal("#AABBCC", scheme.GetProperty("red").GetString());

        var profile = doc.RootElement.GetProperty("profiles")[0];
        Assert.False(profile.TryGetProperty("font", out _));
        Assert.False(profile.TryGetProperty("opacity", out _));
        Assert.False(profile.TryGetProperty("padding", out _));
        Assert.False(profile.TryGetProperty("backgroundImage", out _));
    }

    [Fact]
    public void InstallerFragmentUsesTheCommandlineVerbatim()
    {
        var json = WindowsTerminalProfile.BuildFragment("\"[INSTALLFOLDER]pickle.exe\" --nologo");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("\"[INSTALLFOLDER]pickle.exe\" --nologo", doc.RootElement.GetProperty("profiles")[0].GetProperty("commandline").GetString());

        var path = Path.Combine(_root, "msi", "Fragments", "Pickle", "pickle.json");
        Assert.Equal(0, WindowsTerminalProfile.WriteFragment(path, "\"C:\\Program Files\\Pickle\\pickle.exe\"", fontInstalled: _ => true));
        Assert.Equal(WindowsTerminalProfile.BuildFragment("\"C:\\Program Files\\Pickle\\pickle.exe\""), File.ReadAllText(path));
        Assert.NotEqual(0xEF, File.ReadAllBytes(path)[0]);
        Assert.Equal(2, WindowsTerminalProfile.WriteFragment(path, " "));

        Assert.Equal(0, WindowsTerminalProfile.WriteFragment(path, "pickle.exe", icon: "%ProgramFiles%\\Pickle\\pickle.png"));
        using var withIcon = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("%ProgramFiles%\\Pickle\\pickle.png", withIcon.RootElement.GetProperty("profiles")[0].GetProperty("icon").GetString());
    }

    [Fact]
    public void InstallerFragmentLeavesOutAMissingFont()
    {
        var path = Path.Combine(_root, "msi", "pickle.json");

        Assert.Equal(0, WindowsTerminalProfile.WriteFragment(path, "pickle.exe", fontInstalled: _ => false));
        Assert.DoesNotContain("\"face\"", File.ReadAllText(path), StringComparison.Ordinal);

        Assert.Equal(0, WindowsTerminalProfile.WriteFragment(path, "pickle.exe", fontInstalled: f => f == "Cascadia Code NF"));
        Assert.Contains("\"face\": \"Cascadia Code NF\"", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void RestoringTheDefaultProfileUsesPicklesBackup()
    {
        var locations = WindowsTerminalLocations.FromLocalAppData(_root);
        var settings = locations.SettingsFiles[0];
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        const string Mine = "{574e775e-4f2a-5b96-ac1e-a2962a402336}";
        File.WriteAllText(settings, $"{{\n    // keep me\n    \"defaultProfile\": \"{Mine}\"\n}}\n");
        var manager = new WindowsTerminalManager(locations, () => new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero));
        manager.Install(Exe, new TerminalSettings(), new TerminalPalette());
        Assert.True(Assert.Single(manager.SetDefaultProfile()).Changed);

        Assert.True(Assert.Single(manager.RestoreDefaultProfile()).Changed);

        Assert.Contains($"\"defaultProfile\": \"{Mine}\"", File.ReadAllText(settings), StringComparison.Ordinal);
        Assert.Contains("// keep me", File.ReadAllText(settings), StringComparison.Ordinal);
        Assert.Empty(manager.RestoreDefaultProfile());

        foreach (var backup in Directory.GetFiles(Path.GetDirectoryName(settings)!, "*.bak"))
        {
            File.Delete(backup);
        }

        File.WriteAllText(settings, $"{{ \"defaultProfile\": \"{WindowsTerminalFragment.ProfileGuidString}\" }}");
        manager.RestoreDefaultProfile();
        Assert.Contains(WindowsTerminalManager.WindowsPowerShellProfileGuid, File.ReadAllText(settings), StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedIconIsAPng()
    {
        var png = WindowsTerminalManager.IconPng();
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], png[..4]);
    }

    [Fact]
    public void InstallUpdateUninstall()
    {
        var locations = WindowsTerminalLocations.FromLocalAppData(_root);
        var manager = new WindowsTerminalManager(locations);
        Assert.False(manager.IsInstalled);
        Assert.False(manager.Update(new TerminalSettings(), new TerminalPalette()));

        Assert.True(manager.Install(Exe, new TerminalSettings(), new TerminalPalette()));
        Assert.True(File.Exists(Path.Combine(_root, "Microsoft", "Windows Terminal", "Fragments", "Pickle", "pickle.json")));
        Assert.Equal(Exe, manager.InstalledExecutable);
        Assert.Equal(WindowsTerminalManager.IconPng(), File.ReadAllBytes(locations.IconFile));
        using (var doc = JsonDocument.Parse(File.ReadAllText(locations.FragmentFile)))
        {
            Assert.Equal(locations.IconFile, doc.RootElement.GetProperty("profiles")[0].GetProperty("icon").GetString());
        }

        Assert.False(manager.Install(Exe, new TerminalSettings(), new TerminalPalette()));

        Assert.True(manager.Update(new TerminalSettings { FontSize = 15 }, new TerminalPalette()));
        Assert.Contains("\"size\": 15", File.ReadAllText(locations.FragmentFile), StringComparison.Ordinal);
        Assert.Equal(Exe, manager.InstalledExecutable);

        Assert.True(manager.Uninstall());
        Assert.False(Directory.Exists(locations.FragmentDirectory));
        Assert.False(manager.Uninstall());
    }

    [Fact]
    public void CommandLineInstallReadsConfigAndThemeFromDisk()
    {
        var paths = new PicklePaths(Path.Combine(_root, "config"), Path.Combine(_root, "data"));
        paths.EnsureCreated();
        File.WriteAllText(paths.ConfigFile, """{ "theme": "mine", "terminal": { "fontFace": "Fira Code", "opacity": 80 } }""");
        File.WriteAllText(paths.LocalConfigFile, """{ "terminal": { "opacity": 70 } }""");
        File.WriteAllText(Path.Combine(paths.ThemesDir, "mine.json"), """{ "terminal": { "background": "#101010" } }""");
        var locations = WindowsTerminalLocations.FromLocalAppData(Path.Combine(_root, "local"));
        var output = new StringWriter();

        Assert.Equal(0, WindowsTerminalProfile.Install(Exe, output, TextWriter.Null, locations, paths));
        var json = File.ReadAllText(locations.FragmentFile);
        Assert.Contains("\"face\": \"Fira Code\"", json, StringComparison.Ordinal);
        Assert.Contains("\"opacity\": 70", json, StringComparison.Ordinal);
        Assert.Contains("\"background\": \"#101010\"", json, StringComparison.Ordinal);
        Assert.Contains("Installed", output.ToString(), StringComparison.Ordinal);

        Assert.Equal(0, WindowsTerminalProfile.Uninstall(output, TextWriter.Null, locations));
        Assert.False(File.Exists(locations.FragmentFile));
        Assert.Equal(1, WindowsTerminalProfile.Install(Exe, TextWriter.Null, TextWriter.Null, null, paths));
    }

    [Fact]
    public void DefaultProfileEditPreservesComments()
    {
        var settings = """
            // This file was initially generated by Windows Terminal
            {
                "$help": "https://aka.ms/terminal-documentation",
                // The default profile: "don't touch // this"
                "defaultProfile": "{61c54bbd-c2c6-5271-96e7-009a87ff44bf}", /* trailing */
                "profiles":
                {
                    "defaults": {},
                    "list":
                    [
                        { "guid": "{61c54bbd-c2c6-5271-96e7-009a87ff44bf}", "name": "Windows PowerShell", "defaultProfile": "nested" },
                    ],
                },
            }
            """;
        var updated = JsoncEditor.SetRootString(settings, "defaultProfile", WindowsTerminalFragment.ProfileGuidString);
        Assert.Equal(
            settings.Replace("\"{61c54bbd-c2c6-5271-96e7-009a87ff44bf}\", /* trailing */", $"\"{WindowsTerminalFragment.ProfileGuidString}\", /* trailing */", StringComparison.Ordinal),
            updated);
        Assert.Equal(WindowsTerminalFragment.ProfileGuidString, JsoncEditor.GetRootString(updated, "defaultProfile"));
        Snapshot.Match(updated);
    }

    [Fact]
    public void DefaultProfileIsInsertedWhenMissing()
    {
        Assert.Equal("{\r\n  \"defaultProfile\": \"x\",\r\n  \"a\": 1\r\n}", JsoncEditor.SetRootString("{\r\n  \"a\": 1\r\n}", "defaultProfile", "x"));
        Assert.Equal("{\n    \"defaultProfile\": \"x\"\n}", JsoncEditor.SetRootString("{}", "defaultProfile", "x"));
        Assert.Throws<FormatException>(() => JsoncEditor.SetRootString("[1]", "defaultProfile", "x"));
    }

    [Fact]
    public void SetDefaultProfileBacksUpEveryInstalledTerminal()
    {
        var locations = WindowsTerminalLocations.FromLocalAppData(_root);
        var stable = locations.SettingsFiles[0];
        var unpackaged = locations.SettingsFiles[2];
        Directory.CreateDirectory(Path.GetDirectoryName(stable)!);
        Directory.CreateDirectory(Path.GetDirectoryName(unpackaged)!);
        File.WriteAllText(stable, "{\n  // mine\n  \"defaultProfile\": \"{0}\"\n}\n");
        File.WriteAllBytes(unpackaged, [0xEF, 0xBB, 0xBF, .. "{ \"defaultProfile\": \"Ubuntu\" }"u8]);
        var manager = new WindowsTerminalManager(locations, () => new DateTimeOffset(2026, 9, 23, 10, 15, 0, TimeSpan.Zero));

        var results = manager.SetDefaultProfile();
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Changed && r.Error is null));
        Assert.Equal("{\n  // mine\n  \"defaultProfile\": \"{0}\"\n}\n", File.ReadAllText(stable + ".pickle-20260923-101500.bak"));
        Assert.Contains("// mine", File.ReadAllText(stable), StringComparison.Ordinal);
        Assert.Equal([0xEF, 0xBB, 0xBF], File.ReadAllBytes(unpackaged)[..3]);
        Assert.All(manager.SettingsStatus(), s => Assert.True(s.IsDefault));

        Assert.All(manager.SetDefaultProfile(), r => Assert.False(r.Changed));
    }

    [Fact]
    public async Task PkTerminalCommands()
    {
        using var t = TestPickle.Create();
        var locations = WindowsTerminalLocations.FromLocalAppData(_root);
        WindowsTerminalIntegration.Register(t.Runtime, locations, () => Exe);
        var command = t.Runtime.CommandRegistry.Get("terminal")!;

        var (code, output) = await Run(t, command, "status");
        Assert.Equal(0, code);
        Assert.Contains("not installed", output, StringComparison.Ordinal);

        (code, _) = await Run(t, command, "install");
        Assert.Equal(0, code);
        Assert.Contains("#1B1F1A", File.ReadAllText(locations.FragmentFile), StringComparison.Ordinal);

        (code, _) = await Run(t, command, "set", "fontsize", "14");
        Assert.Equal(0, code);
        Assert.Equal(14, t.Runtime.Config.Current.Terminal.FontSize);
        Assert.Contains("\"size\": 14", File.ReadAllText(locations.FragmentFile), StringComparison.Ordinal);

        (code, _) = await Run(t, command, "set", "font", "JetBrains", "Mono", "NF");
        Assert.Equal("JetBrains Mono NF", t.Runtime.Config.Current.Terminal.FontFace);
        (code, _) = await Run(t, command, "set", "opacity", "85%");
        Assert.Equal(85, t.Runtime.Config.Current.Terminal.Opacity);
        (code, _) = await Run(t, command, "set", "acrylic", "on");
        Assert.True(t.Runtime.Config.Current.Terminal.UseAcrylic);
        (code, _) = await Run(t, command, "set", "cursor", "filledbox");
        Assert.Equal("filledBox", t.Runtime.Config.Current.Terminal.CursorShape);
        (code, _) = await Run(t, command, "set", "padding", "8, 4");
        Assert.Equal("8, 4", t.Runtime.Config.Current.Terminal.Padding);
        (code, _) = await Run(t, command, "set", "background", Path.Combine(_root, "bg.png"), "0.3");
        Assert.Equal(0.3, t.Runtime.Config.Current.Terminal.BackgroundImageOpacity);
        var fragment = File.ReadAllText(locations.FragmentFile);
        Assert.Contains("\"cursorShape\": \"filledBox\"", fragment, StringComparison.Ordinal);
        Assert.Contains("\"backgroundImageOpacity\": 0.3", fragment, StringComparison.Ordinal);
        (code, _) = await Run(t, command, "set", "background", "none");
        Assert.Null(t.Runtime.Config.Current.Terminal.BackgroundImage);

        (code, output) = await Run(t, command, "set", "opacity", "250");
        Assert.Equal(2, code);
        Assert.Contains("0 to 100", output, StringComparison.Ordinal);
        (code, _) = await Run(t, command, "set", "cursor", "triangle");
        Assert.Equal(2, code);
        (code, _) = await Run(t, command, "set", "padding", "1,2,3");
        Assert.Equal(2, code);

        // Theme and config changes regenerate the installed fragment.
        t.Runtime.ThemeProvider.Apply("classic");
        Assert.Contains("\"background\": \"#012456\"", File.ReadAllText(locations.FragmentFile), StringComparison.Ordinal);
        t.Runtime.Config.SetValue("terminal.fontSize", "20");
        Assert.Contains("\"size\": 20", File.ReadAllText(locations.FragmentFile), StringComparison.Ordinal);

        (code, output) = await Run(t, command, "default");
        Assert.Equal(1, code);
        Assert.Contains("settings.json was not found", output, StringComparison.Ordinal);
        var settings = locations.SettingsFiles[1];
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        File.WriteAllText(settings, "{ \"defaultProfile\": \"{0}\" }");
        (code, output) = await Run(t, command, "default");
        Assert.Equal(0, code);
        Assert.Contains("now the default profile", output, StringComparison.Ordinal);

        (code, _) = await Run(t, command, "uninstall");
        Assert.Equal(0, code);
        Assert.False(File.Exists(locations.FragmentFile));
    }

    [Fact]
    public void FirstRunOffersTheProfileWhenTerminalIsPresentAndPickleIsNotInIt()
    {
        using var t = TestPickle.Create();
        var locations = WindowsTerminalLocations.FromLocalAppData(_root) with { MachineFragmentFile = Path.Combine(_root, "machine", "pickle.json") };
        WindowsTerminalIntegration.Register(t.Runtime, locations, () => Exe);
        var offer = Assert.Single(t.Runtime.Services.Require<IFirstRunOffers>().All, o => o.Id == "windows-terminal");
        Assert.Equal(2, offer.Since);
        if (Environment.GetEnvironmentVariable("WT_SESSION") is null)
        {
            Assert.False(offer.IsRelevant());
        }

        Directory.CreateDirectory(Path.GetDirectoryName(locations.SettingsFiles[0])!);
        Assert.True(offer.IsRelevant());
        Assert.Contains("Added the 'Pickle' profile", offer.Accept(), StringComparison.Ordinal);
        Assert.Equal(Exe, new WindowsTerminalManager(locations).InstalledExecutable);
        Assert.False(offer.IsRelevant());
    }

    [Fact]
    public void WithTheInstallersProfileTheOfferLayersAPerUserProfileOverIt()
    {
        using var t = TestPickle.Create();
        var locations = WindowsTerminalLocations.FromLocalAppData(_root) with { MachineFragmentFile = Path.Combine(_root, "machine", "pickle.json") };
        Directory.CreateDirectory(Path.Combine(_root, "machine"));
        File.WriteAllText(locations.MachineFragmentFile!, "{}");
        Directory.CreateDirectory(Path.GetDirectoryName(locations.SettingsFiles[0])!);

        WindowsTerminalIntegration.Register(t.Runtime, locations, () => Exe);

        var offer = Assert.Single(t.Runtime.Services.Require<IFirstRunOffers>().All, o => o.Id == "windows-terminal");
        Assert.Contains("in step with your theme and font", offer.Question, StringComparison.Ordinal);
        Assert.True(offer.IsRelevant());
    }

    [Fact]
    public void DefaultProfileOfferPointsTerminalAtPickle()
    {
        using var t = TestPickle.Create();
        var locations = WindowsTerminalLocations.FromLocalAppData(_root);
        WindowsTerminalIntegration.Register(t.Runtime, locations, () => Exe);
        var offer = Assert.Single(t.Runtime.Services.Require<IFirstRunOffers>().All, o => o.Id == "windows-terminal-default");
        var settings = locations.SettingsFiles[0];
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        File.WriteAllText(settings, "{\n    // mine\n    \"defaultProfile\": \"{61c54bbd-c2c6-5271-96e7-009a87ff44bf}\"\n}\n");
        Assert.False(offer.IsRelevant()); // no Pickle profile yet

        new WindowsTerminalManager(locations).Install(Exe, new TerminalSettings(), new TerminalPalette());
        Assert.True(offer.IsRelevant());
        Assert.Contains("default profile", offer.Accept(), StringComparison.Ordinal);
        Assert.Contains(WindowsTerminalFragment.ProfileGuidString, File.ReadAllText(settings), StringComparison.Ordinal);
        Assert.Contains("// mine", File.ReadAllText(settings), StringComparison.Ordinal);
        Assert.False(offer.IsRelevant());
    }

    [Fact]
    public void NerdFontOfferInstallsTheFontAndRewritesTheProfile()
    {
        using var t = TestPickle.Create();
        var fonts = new Pickle.Testing.Fakes.FakeFontService();
        t.Runtime.Services.Add<Pickle.Abstractions.Services.IFontService>(fonts);
        var locations = WindowsTerminalLocations.FromLocalAppData(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(locations.SettingsFiles[0])!);
        new WindowsTerminalManager(locations).Install(Exe, new TerminalSettings(), new TerminalPalette());

        WindowsTerminalIntegration.Register(t.Runtime, locations, () => Exe);

        Assert.Null(ProfileFont(locations));
        var offer = Assert.Single(t.Runtime.Services.Require<IFirstRunOffers>().All, o => o.Id == "nerd-font");
        Assert.True(offer.IsRelevant());
        Assert.Contains("Restart Windows Terminal", offer.Accept(), StringComparison.Ordinal);
        Assert.Equal("Cascadia Code NF", ProfileFont(locations));
        Assert.False(offer.IsRelevant());
    }

    [Fact]
    public void AProfileNamingAMissingFontIsRepairedAtStartup()
    {
        using var t = TestPickle.Create();
        var fonts = new Pickle.Testing.Fakes.FakeFontService();
        t.Runtime.Services.Add<Pickle.Abstractions.Services.IFontService>(fonts);
        var locations = WindowsTerminalLocations.FromLocalAppData(_root);
        new WindowsTerminalManager(locations).Install(Exe, new TerminalSettings { FontFace = "Cascadia Code NF" }, new TerminalPalette());
        Assert.Equal("Cascadia Code NF", ProfileFont(locations));

        WindowsTerminalIntegration.Register(t.Runtime, locations, () => Exe);

        Assert.Null(ProfileFont(locations));
        Assert.Equal("Cascadia Code NF", t.Runtime.Config.Current.Terminal.FontFace);
    }

    [Fact]
    public void AnElevatedSessionKeepsTheConfiguredThemesColoursInTheProfile()
    {
        using var t = TestPickle.Create(elevated: true);
        Assert.Equal("admin", t.Runtime.Themes.Current.Name);
        var locations = WindowsTerminalLocations.FromLocalAppData(_root);
        new WindowsTerminalManager(locations).Install(Exe, new TerminalSettings(), new TerminalPalette { Background = "#000000" });

        WindowsTerminalIntegration.Register(t.Runtime, locations, () => Exe);

        using var doc = JsonDocument.Parse(File.ReadAllText(locations.FragmentFile));
        Assert.Equal(t.Runtime.Themes.Load("pickle")!.Terminal.Background, doc.RootElement.GetProperty("schemes")[0].GetProperty("background").GetString());
    }

    [Theory]
    [InlineData("Cascadia Code NF", "Cascadia Code NF")]
    [InlineData("Nope Mono", null)]
    [InlineData("Nope Mono, Cascadia Mono", "Cascadia Mono")]
    [InlineData(null, null)]
    public void FragmentsOnlyNameInstalledFonts(string? configured, string? written)
    {
        var settings = new TerminalSettings { FontFace = configured, FontSize = 13, Padding = "4" };

        var result = TerminalFonts.ForFragment(settings, face => face is "Cascadia Code NF" or "Cascadia Mono");

        Assert.Equal(written, result.FontFace);
        Assert.Equal((13d, "4"), (result.FontSize, result.Padding));
        Assert.Equal(configured, settings.FontFace);
    }

    [Fact]
    public void TerminalFontProbeFollowsTerminalsLayering()
    {
        var locations = WindowsTerminalLocations.FromLocalAppData(_root) with { MachineFragmentFile = Path.Combine(_root, "machine", "pickle.json") };
        var settings = locations.SettingsFiles[0];
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        Directory.CreateDirectory(Path.Combine(_root, "machine"));
        const string Other = "{61c54bbd-c2c6-5271-96e7-009a87ff44bf}";
        var pickle = WindowsTerminalFragment.ProfileGuidString;
        var env = new Dictionary<string, string?> { ["WT_SESSION"] = "x" };
        string? Face() => TerminalFontProbe.WindowsTerminalFace(locations, n => env.GetValueOrDefault(n));

        Assert.Equal("Cascadia Mono", Face());
        File.WriteAllText(settings, $$"""
            {
                // comments and trailing commas are fine
                "profiles": {
                    "defaults": { "font": { "face": "Consolas" } },
                    "list": [ { "guid": "{{Other}}", "font": { "face": "Hack NF" }, }, { "guid": "{{pickle}}" } ]
                }
            }
            """);
        Assert.Equal("Consolas", Face());
        env["WT_PROFILE_ID"] = Other;
        Assert.Equal("Hack NF", Face());

        env["WT_PROFILE_ID"] = pickle;
        Assert.Equal("Consolas", Face());
        File.WriteAllText(locations.MachineFragmentFile!, WindowsTerminalFragment.Build(Exe, new TerminalSettings { FontFace = "Machine NF" }, new TerminalPalette()));
        Assert.Equal("Machine NF", Face());
        new WindowsTerminalManager(locations).Install(Exe, new TerminalSettings { FontFace = "User NF" }, new TerminalPalette());
        Assert.Equal("User NF", Face());

        env.Remove("WT_SESSION");
        Assert.Null(Face());
    }

    private static string? ProfileFont(WindowsTerminalLocations locations)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(locations.FragmentFile));
        return doc.RootElement.GetProperty("profiles")[0].TryGetProperty("font", out var font) && font.TryGetProperty("face", out var face)
            ? face.GetString()
            : null;
    }

    [Fact]
    public async Task PkTerminalReportsWindowsOnlyWithoutLocations()
    {
        using var t = TestPickle.Create();
        WindowsTerminalIntegration.Register(t.Runtime, locations: null);
        var (code, output) = await Run(t, t.Runtime.CommandRegistry.Get("terminal")!, "install");
        Assert.Equal(1, code);
        Assert.Contains("only available on Windows", output, StringComparison.Ordinal);
    }

    private static async Task<(int Code, string Output)> Run(TestPickle t, IPickleCommand command, params string[] args)
    {
        var lines = new List<string>();
        var context = new PickleCommandContext
        {
            Pickle = t.Runtime,
            WriteObject = o => lines.Add(o?.ToString() ?? string.Empty),
            WriteHost = lines.Add,
            WriteError = e => lines.Add("error: " + e),
            Confirm = (_, d) => d,
            Cwd = Path.GetTempPath(),
        };
        var code = await command.ExecuteAsync(context, args, CancellationToken.None);
        return (code, TextWidth.StripAnsi(string.Join('\n', lines)));
    }
}
