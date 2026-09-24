using Pickle.Abstractions;
using Pickle.Core.Plugins;
using Pickle.Testing;

namespace Pickle.Core.Tests.Plugins;

public class PluginLoadingTests
{
    private static readonly string PsFixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "SamplePsPlugin");
    private static readonly string DotnetFixture = Path.Combine(AppContext.BaseDirectory, "fixtures-dotnet", "SampleDotnetPlugin.dll");

    internal static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }
    }

    private static TestPickle StartWith(Action<PicklePaths> arrange, Action<PickleConfig>? configure = null)
    {
        var t = TestPickle.Create(configure: configure);
        arrange(t.Paths);
        t.Runtime.InitializeComponents();
        t.Runtime.Start([]);
        return t;
    }

    private static PluginManager Manager(TestPickle t) => (PluginManager)t.Runtime.Plugins;

    [Fact]
    public async Task PowerShellPluginFromThePluginsFolderRegistersItsContributions()
    {
        using var t = StartWith(p => CopyDirectory(PsFixture, Path.Combine(p.PluginsDir, "SamplePsPlugin")));

        var info = Assert.Single(Manager(t).Plugins, p => p.Id == "SamplePsPlugin");
        Assert.Equal(PluginStatus.Loaded, info.Status);
        Assert.Equal(PluginInfo.PowerShellKind, info.Kind);
        Assert.Equal("1.2.3", info.Version);
        Assert.Contains("command: sample-hello", info.Contributions);
        Assert.Contains("segment: sample", info.Contributions);
        Assert.Contains("hook: PostExecute", info.Contributions);
        Assert.Contains("panel: sample.panel", info.Contributions);

        // pk command runs in the module's scope (module-private helper) and gets its args.
        Assert.Equal(["HELLO, BOB!"], t.Run("pk sample-hello Bob --shout"));
        Assert.Equal(["Hello, world!"], t.Run("pk sample-hello"));

        // Prompt segment renders through the main runspace.
        var segment = t.Runtime.PromptSegmentRegistry.Get("sample")!;
        var output = await segment.RenderAsync(t.Runtime.CreatePromptContext(), new SegmentStyle { Type = "sample" }, CancellationToken.None);
        Assert.Equal(new PromptSegmentOutput("sample:0", "#FF0000"), output);

        // Hooks fire with $PickleEvent.
        t.Runtime.Repl.ExecuteLine("$sampleProbe = 1", echo: false);
        Assert.Equal(["$sampleProbe = 1"], t.Run("$global:SamplePluginLastCommand"));

        var panel = Assert.Single(t.Runtime.PanelRegistry.ListPanels, p => p.Id == "sample.panel");
        Assert.Equal("Alt+Y", panel.DefaultKey);
        Assert.Contains("'one'", panel.ItemsScript, StringComparison.Ordinal);
        Assert.Contains("Write-Output", panel.Actions["Echo"], StringComparison.Ordinal);

        var completion = t.Runtime.CompletionRegistry.Providers.Single(p => p.Name == "sample-targets");
        var set = await completion.GetCompletionsAsync(new CompletionRequest("deploy st", 9, t.Paths.ConfigDir), CancellationToken.None);
        var item = Assert.Single(set!.Items);
        Assert.Equal("staging", item.CompletionText);
        Assert.Equal("deploy to staging", item.Description);
        Assert.Equal(7, set.ReplacementIndex);
        Assert.Null(await completion.GetCompletionsAsync(new CompletionRequest("git st", 6, t.Paths.ConfigDir), CancellationToken.None));

        var rewriter = t.Runtime.TranslationRegistry.Rewriters.Single(r => r.Name == "sample-please");
        Assert.Equal("Get-Date", rewriter.Rewrite("please Get-Date", new RewriteContext(t.Paths.ConfigDir, []))!.Rewritten);

        Assert.Equal(["Loaded"], t.Run("(Get-PicklePlugin SamplePsPlugin).Status.ToString()"));
    }

    [Fact]
    public void DisabledPluginsAndNoPluginsModeSkipThirdPartyPlugins()
    {
        using (var t = StartWith(p => CopyDirectory(PsFixture, Path.Combine(p.PluginsDir, "SamplePsPlugin")), c => c.Plugins.Disabled.Add("SamplePsPlugin")))
        {
            Assert.Equal(PluginStatus.Disabled, Manager(t).Find("SamplePsPlugin")!.Status);
            Assert.Null(t.Runtime.CommandRegistry.Get("sample-hello"));
        }

        using (var t = StartWith(p => CopyDirectory(PsFixture, Path.Combine(p.PluginsDir, "SamplePsPlugin")), c => c.Plugins.AutoLoad = false))
        {
            Assert.Null(Manager(t).Find("SamplePsPlugin"));
        }
    }

    [Fact]
    public void BrokenPluginsAreIsolated()
    {
        using var t = StartWith(p =>
        {
            var broken = Directory.CreateDirectory(Path.Combine(p.PluginsDir, "Broken")).FullName;
            File.WriteAllText(Path.Combine(broken, "Broken.psm1"), "throw 'boom'");
            CopyDirectory(PsFixture, Path.Combine(p.PluginsDir, "SamplePsPlugin"));
        });

        var broken = Manager(t).Find("Broken")!;
        Assert.Equal(PluginStatus.Failed, broken.Status);
        Assert.Contains("boom", broken.Error, StringComparison.Ordinal);
        Assert.Equal(PluginStatus.Loaded, Manager(t).Find("SamplePsPlugin")!.Status);
    }

    [Fact]
    public void DotnetPluginRequiresTrustThenLoads()
    {
        using var t = StartWith(p =>
        {
            var dir = Directory.CreateDirectory(Path.Combine(p.PluginsDir, "SampleDotnetPlugin")).FullName;
            File.Copy(DotnetFixture, Path.Combine(dir, "SampleDotnetPlugin.dll"));
        });

        var untrusted = Manager(t).Find("SampleDotnetPlugin")!;
        Assert.Equal(PluginStatus.Untrusted, untrusted.Status);
        Assert.Contains("Plugin SampleDotnetPlugin is not trusted. Run: pk plugin trust SampleDotnetPlugin", t.Terminal.RawOutput, StringComparison.Ordinal);
        Assert.Null(t.Runtime.CommandRegistry.Get("dotnet-hello"));

        t.Run("pk plugin trust SampleDotnetPlugin");
        var hash = AssemblyInspector.Sha256(DotnetFixture);
        Assert.Contains(hash, t.Runtime.Config.Current.Plugins.TrustedAssemblies);
        Assert.Contains(hash, File.ReadAllText(t.Paths.ConfigFile), StringComparison.Ordinal);
        Assert.Equal(PluginStatus.Loaded, Manager(t).Find("sample.dotnet")!.Status);
        Assert.Equal(["hello from dotnet"], t.Run("pk dotnet-hello"));
    }

    [Fact]
    public void TrustedDotnetPluginWithManifestLoadsAtStartupInItsOwnLoadContext()
    {
        var hash = AssemblyInspector.Sha256(DotnetFixture);
        using var t = StartWith(
            p =>
            {
                var dir = Directory.CreateDirectory(Path.Combine(p.PluginsDir, "sample")).FullName;
                File.Copy(DotnetFixture, Path.Combine(dir, "SampleDotnetPlugin.dll"));
                File.WriteAllText(Path.Combine(dir, "plugin.json"), """{ "id": "sample", "assembly": "SampleDotnetPlugin.dll", "type": "SampleDotnetPlugin.SamplePlugin" }""");
            },
            c => c.Plugins.TrustedAssemblies.Add("sha256:" + hash.ToLowerInvariant()));

        var info = Manager(t).Find("sample.dotnet")!;
        Assert.Equal(PluginStatus.Loaded, info.Status);
        Assert.Contains("command: dotnet-hello", info.Contributions);
        Assert.Equal(["hello from dotnet"], t.Run("pk dotnet-hello"));
        var loaded = AppDomain.CurrentDomain.GetAssemblies().Last(a => a.GetName().Name == "SampleDotnetPlugin");
        Assert.IsType<PluginLoadContext>(System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(loaded));
    }

    [Fact]
    public void ModulesOnPSModulePathWithPickleKeyAreDiscovered()
    {
        var name = "PickleModulePathProbe" + Guid.NewGuid().ToString("N")[..8];
        var root = Directory.CreateTempSubdirectory("pickle-modpath").FullName;
        var moduleDir = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        File.WriteAllText(Path.Combine(moduleDir, name + ".psm1"), "function Get-" + name + " { 'probe' }");
        File.WriteAllText(Path.Combine(moduleDir, name + ".psd1"), $$"""
            @{ RootModule = '{{name}}.psm1'; ModuleVersion = '1.0.0'; FunctionsToExport = '*'; PrivateData = @{ Pickle = @{} } }
            """);
        var original = Environment.GetEnvironmentVariable("PSModulePath");
        Environment.SetEnvironmentVariable("PSModulePath", original + Path.PathSeparator + root);
        try
        {
            using var t = TestPickle.Create(start: true);
            var info = Manager(t).Find(name);
            Assert.NotNull(info);
            Assert.Equal(PluginStatus.Loaded, info.Status);
            Assert.Equal(["probe"], t.Run("Get-" + name));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSModulePath", (Environment.GetEnvironmentVariable("PSModulePath") ?? string.Empty).Replace(Path.PathSeparator + root, string.Empty, StringComparison.Ordinal));
            Directory.Delete(root, recursive: true);
        }
    }
}
