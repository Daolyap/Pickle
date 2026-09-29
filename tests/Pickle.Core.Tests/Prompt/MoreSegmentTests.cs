using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Core.Prompt.Segments;

namespace Pickle.Core.Tests.Prompt;

public class MoreSegmentTests
{
    [Theory]
    [InlineData("prod", "eu-west-1", true, "prod (eu-west-1)")]
    [InlineData("prod", "eu-west-1", false, "prod")]
    [InlineData("dev", null, true, "dev")]
    [InlineData(null, "eu-west-1", true, null)]
    [InlineData("  ", null, true, null)]
    [InlineData("bad\u001b[31m", null, true, "bad?[31m")]
    public void AwsShowsTheProfileAndRegion(string? profile, string? region, bool showRegion, string? expected) =>
        Assert.Equal(expected, AwsSegment.Format(profile, region, showRegion));

    [Fact]
    public async Task AwsReadsTheEnvironmentAndHidesWithoutAProfile()
    {
        var env = new Dictionary<string, string> { ["AWS_DEFAULT_PROFILE"] = "ops", ["AWS_REGION"] = "us-east-2" };
        Assert.Equal("ops (us-east-2)", (await new AwsSegment(Environment(env)).RenderAsync(Context(), new SegmentStyle(), default))?.Text);
        Assert.Null(await new AwsSegment(Environment([])).RenderAsync(Context(), new SegmentStyle(), default));
    }

    [Fact]
    public void AzureTakesTheDefaultSubscriptionFromAProfileWithABom()
    {
        const string profile = "﻿{\"installationId\":\"x\",\"subscriptions\":[{\"name\":\"Dev\",\"isDefault\":false},{\"name\":\"Production\",\"isDefault\":true}]}";
        Assert.Equal("Production", AzureSegment.ParseProfile(profile));
        Assert.Null(AzureSegment.ParseProfile("{\"subscriptions\":[]}"));
        Assert.Null(AzureSegment.ParseProfile("not json"));
    }

    [Fact]
    public void GcloudReadsTheCoreProject()
    {
        Assert.Equal("my-proj-123", GcloudSegment.ParseProject("[core]\naccount = me@example.com\nproject = my-proj-123\n\n[compute]\nregion = x\n"));
        Assert.Null(GcloudSegment.ParseProject("[compute]\nproject = not-core\n"));
    }

    [Fact]
    public async Task DockerPrefersTheEnvironmentAndHidesTheDefaultContext()
    {
        Assert.Equal("desktop-linux", DockerSegment.ParseConfig("{\"auths\":{},\"currentContext\":\"desktop-linux\"}"));
        var fromFile = Environment([], docker: () => "colima");
        var fromEnv = Environment(new Dictionary<string, string> { ["DOCKER_CONTEXT"] = "remote" }, docker: () => "colima");
        Assert.Equal("colima", (await new DockerSegment(fromFile).RenderAsync(Context(), new SegmentStyle(), default))?.Text);
        Assert.Equal("remote", (await new DockerSegment(fromEnv).RenderAsync(Context(), new SegmentStyle(), default))?.Text);
        Assert.Null(await new DockerSegment(Environment([], docker: () => "default")).RenderAsync(Context(), new SegmentStyle(), default));
    }

    [Fact]
    public void CloudConfigFilesAreFoundUnderHomeAndReReadWhenTheyChange()
    {
        var home = Directory.CreateTempSubdirectory("pickle-cloud").FullName;
        var reader = new CloudConfigReader(_ => null, () => home);
        Assert.Null(reader.AzureSubscription());

        Directory.CreateDirectory(Path.Combine(home, ".azure"));
        var profile = Path.Combine(home, ".azure", "azureProfile.json");
        File.WriteAllText(profile, "{\"subscriptions\":[{\"name\":\"One\",\"isDefault\":true}]}");
        Assert.Equal("One", reader.AzureSubscription());
        File.WriteAllText(profile, "{\"subscriptions\":[{\"name\":\"Second one\",\"isDefault\":true}]}");
        File.SetLastWriteTimeUtc(profile, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal("Second one", reader.AzureSubscription());

        var gcloud = OperatingSystem.IsWindows() ? null : Path.Combine(home, ".config", "gcloud");
        if (gcloud is not null)
        {
            Directory.CreateDirectory(Path.Combine(gcloud, "configurations"));
            File.WriteAllText(Path.Combine(gcloud, "active_config"), "work\n");
            File.WriteAllText(Path.Combine(gcloud, "configurations", "config_work"), "[core]\nproject = work-project\n");
            Assert.Equal("work-project", reader.GcloudProject());
        }

        Directory.CreateDirectory(Path.Combine(home, ".docker"));
        File.WriteAllText(Path.Combine(home, ".docker", "config.json"), "{\"currentContext\":\"rootless\"}");
        Assert.Equal("rootless", reader.DockerContext());
    }

    [Theory]
    [InlineData("module x\n\ngo 1.22\n\ntoolchain go1.22.3\n", "1.22.3")]
    [InlineData("module x\ngo 1.21\n", "1.21")]
    [InlineData("module x\n", null)]
    public void GoVersionComesFromGoMod(string goMod, string? expected) => Assert.Equal(expected, Toolchains.ParseGoMod(goMod));

    [Theory]
    [InlineData("[toolchain]\nchannel = \"1.80.1\"\ncomponents = [\"rustfmt\"]\n", "1.80.1")]
    [InlineData("nightly-2024-08-01\n", "nightly-2024-08-01")]
    [InlineData("[toolchain]\nprofile = \"minimal\"\n", null)]
    public void RustChannelComesFromTheToolchainFile(string text, string? expected) => Assert.Equal(expected, Toolchains.ParseRustToolchain(text));

    [Fact]
    public void VersionOutputsAndGlobalJsonParse()
    {
        Assert.Equal("1.80.1", Toolchains.ParseRustc("rustc 1.80.1 (3f5fd8dd4 2024-08-06)\n"));
        Assert.Equal("9.0.100", Toolchains.ParseGlobalJson("{ // pinned\n \"sdk\": { \"version\": \"9.0.100\", \"rollForward\": \"latestFeature\" }, }"));
        Assert.Null(Toolchains.ParseGlobalJson("{ \"msbuild-sdks\": {} }"));
    }

    [Fact]
    public async Task ToolchainsAreFoundFromProjectFiles()
    {
        var root = Directory.CreateTempSubdirectory("pickle-toolchains").FullName;
        var reader = new ToolchainReader();

        var go = Directory.CreateDirectory(Path.Combine(root, "go", "cmd", "app")).FullName;
        File.WriteAllText(Path.Combine(root, "go", "go.mod"), "module x\ngo 1.23\n");
        Assert.Equal("1.23", reader.Go(go));
        Assert.Null(reader.Go(root));

        var rust = Directory.CreateDirectory(Path.Combine(root, "rust", "src")).FullName;
        File.WriteAllText(Path.Combine(root, "rust", "Cargo.toml"), "[package]\nname = \"x\"\n");
        File.WriteAllText(Path.Combine(root, "rust", "rust-toolchain.toml"), "[toolchain]\nchannel = \"stable\"\n");
        Assert.Equal("stable", await reader.RustAsync(rust, default));

        var dotnet = Directory.CreateDirectory(Path.Combine(root, "dotnet")).FullName;
        Assert.Null(await reader.DotnetAsync(dotnet, default));
        File.WriteAllText(Path.Combine(dotnet, "App.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(dotnet, "global.json"), "{ \"sdk\": { \"version\": \"8.0.400\" } }");
        Assert.Equal("8.0.400", await reader.DotnetAsync(dotnet, default));
        Assert.True(Toolchains.IsDotnetProject(dotnet));
        Assert.False(Toolchains.IsDotnetProject(go));
    }

    [Theory]
    [InlineData(57, false, false, 95, "57%")]
    [InlineData(57, true, true, 95, "57%↑")]
    [InlineData(99, false, true, 95, null)]
    [InlineData(99, false, false, 95, "99%")]
    [InlineData(null, false, false, 95, null)]
    public void BatteryFormat(int? percent, bool charging, bool onAc, int hideAbove, string? expected) =>
        Assert.Equal(expected, BatterySegment.Format(new BatteryStatus(percent, charging, onAc), hideAbove));

    [Fact]
    public async Task LowBatteryTurnsRed()
    {
        var segment = new BatterySegment(Environment([], battery: () => new BatteryStatus(9, false, false)));
        var plain = await segment.RenderAsync(Context(), new SegmentStyle { Type = "battery" }, default);
        Assert.Equal(("9%", "red", (string?)null), (plain!.Text, plain.Foreground, plain.Background));

        var block = await segment.RenderAsync(Context(), new SegmentStyle { Type = "battery", Background = "#E5C07B" }, default);
        Assert.Equal("red", block!.Background);

        var none = new BatterySegment(Environment([]));
        Assert.Null(await none.RenderAsync(Context(), new SegmentStyle(), default));
    }

    private static PromptContext Context() =>
        new("/work", true, 0, null, false, 0, "me", "box", new DateTimeOffset(2026, 9, 23, 14, 5, 9, TimeSpan.Zero), 80);

    private static SegmentEnvironment Environment(Dictionary<string, string> env, Func<string?>? docker = null, Func<BatteryStatus?>? battery = null) => new()
    {
        Home = () => null,
        FindRepositoryRoot = _ => null,
        GetEnvironmentVariable = name => env.GetValueOrDefault(name),
        GetGitStatus = (_, _) => Task.FromResult<GitStatus?>(null),
        IsNodeProject = _ => false,
        GetNodeVersion = _ => Task.FromResult<string?>(null),
        GetKubeContext = () => null,
        DurationThresholdMs = () => 2000,
        GetDockerContext = docker ?? (() => null),
        GetBattery = battery ?? (() => null),
    };
}
