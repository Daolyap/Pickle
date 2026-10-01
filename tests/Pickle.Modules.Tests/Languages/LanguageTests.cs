using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Modules.Languages;
using Pickle.Testing;
using Pickle.Testing.Fakes;

namespace Pickle.Modules.Tests.Languages;

public sealed class LanguageTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pickle-lang").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("Python 3.12.1\n", "3.12.1")]
    [InlineData("openjdk version \"21.0.1\" 2023-10-17\nOpenJDK Runtime Environment", "21.0.1")]
    [InlineData("java version \"1.8.0_392\"", "1.8.0")]
    [InlineData("ruby 3.3.0 (2023-12-25 revision 5124f9ac75) [x86_64-linux]", "3.3.0")]
    [InlineData("PHP 8.3.1 (cli) (built: Dec 21 2023)", "8.3.1")]
    [InlineData("deno 1.40.0 (release, x86_64-unknown-linux-gnu)", "1.40.0")]
    [InlineData("1.0.25\n", "1.0.25")]
    [InlineData("Terraform v1.7.0\non linux_amd64\n", "1.7.0")]
    [InlineData("1.16.0\n", "1.16.0")]
    [InlineData("Lua 5.4.6  Copyright (C) 1994-2023 Lua.org, PUC-Rio", "5.4.6")]
    [InlineData("Swift version 5.9.2 (swift-5.9.2-RELEASE)", "5.9.2")]
    [InlineData("0.11.0", "0.11.0")]
    [InlineData("Dart SDK version: 3.2.3 (stable) (Tue Dec 12 2023)", "3.2.3")]
    public void VersionIsTheFirstVersionLikeToken(string output, string expected)
    {
        Assert.Equal(expected, LanguageSpecs.ParseVersion(output));
    }

    [Theory]
    [InlineData("3.12.1\n", null, "3.12.1")]
    [InlineData("v1.7.0", null, "1.7.0")]
    [InlineData("# comment\n\n21.0.1", null, "21.0.1")]
    [InlineData("nodejs 20.1.0\npython 3.11.4\nterraform 1.6.2\n", "python", "3.11.4")]
    [InlineData("nodejs 20.1.0\n", "python", null)]
    [InlineData("system", null, null)]
    public void PinnedVersionsComeFromVersionFilesAndToolVersions(string content, string? key, string? expected) =>
        Assert.Equal(expected, LanguageSpecs.ParsePinned(content, key));

    [Fact]
    public void ProjectsAreFoundUpwardsByMarkerFilesAndGlobs()
    {
        var project = Directory.CreateDirectory(Path.Combine(_root, "app")).FullName;
        var deep = Directory.CreateDirectory(Path.Combine(project, "src", "pkg")).FullName;
        File.WriteAllText(Path.Combine(project, "pyproject.toml"), "[project]");
        File.WriteAllText(Path.Combine(_root, "infra.tf"), string.Empty);
        var python = LanguageSpecs.All.Single(s => s.Type == "python");
        var terraform = LanguageSpecs.All.Single(s => s.Type == "terraform");
        var java = LanguageSpecs.All.Single(s => s.Type == "java");

        Assert.Equal(project, LanguageSegment.FindProject(deep, python));
        Assert.Equal(_root, LanguageSegment.FindProject(deep, terraform));
        Assert.Null(LanguageSegment.FindProject(deep, java));
        Assert.Null(LanguageSegment.FindProject(string.Empty, python));
    }

    private static async Task<string?> Render(TestPickle t, IPromptSegment segment, string cwd)
    {
        var context = t.Runtime.CreatePromptContext() with { Cwd = cwd };
        return (await segment.RenderAsync(context, new SegmentStyle { Type = segment.Type }, CancellationToken.None))?.Text;
    }

    [Fact]
    public async Task ASegmentShowsInsideAProjectAndAsksTheToolOnlyOnce()
    {
        var project = Directory.CreateDirectory(Path.Combine(_root, "py")).FullName;
        File.WriteAllText(Path.Combine(project, "requirements.txt"), "flask");
        var runner = new FakeProgramRunner().On("python3", "--version", "Python 3.12.1\n");
        using var t = TestPickle.Create(start: true, modules: OptionalModules.All, machineModules: ["languages"]);
        t.Runtime.Services.Add<IProgramRunner>(runner);
        var segment = new LanguageSegmentProbe(LanguageSpecs.All.Single(s => s.Type == "python"), runner).Segment;

        Assert.Equal("3.12.1", await Render(t, segment, project));
        Assert.Equal("3.12.1", await Render(t, segment, project));
        Assert.Null(await Render(t, segment, _root));
        Assert.Single(runner.Calls);
        Assert.Equal(["--version"], runner.Calls[0].Arguments);
    }

    [Fact]
    public async Task APinFileWinsWithoutStartingAnything()
    {
        var project = Directory.CreateDirectory(Path.Combine(_root, "rb")).FullName;
        File.WriteAllText(Path.Combine(project, "Gemfile"), string.Empty);
        File.WriteAllText(Path.Combine(project, ".ruby-version"), "3.2.2\n");
        var runner = new FakeProgramRunner().On("ruby", "--version", "ruby 3.3.0");
        using var t = TestPickle.Create(start: true);
        var segment = new LanguageSegmentProbe(LanguageSpecs.All.Single(s => s.Type == "ruby"), runner).Segment;

        Assert.Equal("3.2.2", await Render(t, segment, project));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task JavaReadsTheVersionFromStandardErrorAndAMissingToolHidesTheSegment()
    {
        var project = Directory.CreateDirectory(Path.Combine(_root, "jv")).FullName;
        File.WriteAllText(Path.Combine(project, "pom.xml"), "<project/>");
        var runner = new FakeProgramRunner().On("java", "-version", string.Empty, stderr: "openjdk version \"21.0.1\" 2023-10-17\n");
        using var t = TestPickle.Create(start: true);

        Assert.Equal("21.0.1", await Render(t, new LanguageSegmentProbe(LanguageSpecs.All.Single(s => s.Type == "java"), runner).Segment, project));
        Assert.Null(await Render(t, new LanguageSegmentProbe(LanguageSpecs.All.Single(s => s.Type == "java"), new FakeProgramRunner()).Segment, project));
    }

    [Fact]
    public void TheModuleRegistersASegmentPerLanguage()
    {
        using var t = TestPickle.Create(start: true, modules: OptionalModules.All, machineModules: ["languages"]);

        Assert.All(LanguageSpecs.All, spec => Assert.NotNull(t.Runtime.PromptSegmentRegistry.Get(spec.Type)));
        Assert.Equal(12, LanguageSpecs.All.Count);
    }

    private sealed class LanguageSegmentProbe(LanguageSpec spec, IProgramRunner runner)
    {
        public IPromptSegment Segment { get; } = Create(spec, runner);

        private static IPromptSegment Create(LanguageSpec spec, IProgramRunner runner) =>
            (IPromptSegment)Activator.CreateInstance(typeof(LanguagesModule).Assembly.GetType("Pickle.Modules.Languages.LanguageSegment")!, spec, (Func<IProgramRunner>)(() => runner))!;
    }
}
