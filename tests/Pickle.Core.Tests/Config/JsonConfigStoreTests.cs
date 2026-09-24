using System.Text.Json.Nodes;
using Pickle.Abstractions;
using Pickle.Core.Config;
using Pickle.Core.Logging;

namespace Pickle.Core.Tests.Config;

public sealed class JsonConfigStoreTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("pickle-config").FullName;
    private readonly PicklePaths _paths;

    public JsonConfigStoreTests()
    {
        _paths = new PicklePaths(Path.Combine(_home, "config"), Path.Combine(_home, "data"));
        _paths.EnsureCreated();
    }

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private JsonConfigStore CreateStore() => new(_paths, new FileLogger(null, PickleLogLevel.Error));

    private JsonObject BaseFile() => JsonNode.Parse(File.ReadAllText(_paths.ConfigFile))!.AsObject();

    [Fact]
    public void LocalOverridesApplyButAreNeverWrittenToConfigJson()
    {
        File.WriteAllText(_paths.LocalConfigFile, """{ "theme": "local-theme", "editor": { "bellStyle": "visual" } }""");
        using var store = CreateStore();
        Assert.Equal("local-theme", store.Current.Theme);
        Assert.Equal("visual", store.Current.Editor.BellStyle);

        store.SetValue("editor.autosuggestions", "false");
        store.Update(c => c.Prompt.GitTimeoutMs = 999);
        store.SetLocalValue("prompt.transientPrompt", "false");

        var written = File.ReadAllText(_paths.ConfigFile);
        Assert.DoesNotContain("local-theme", written, StringComparison.Ordinal);
        Assert.DoesNotContain("visual", written, StringComparison.Ordinal);
        Assert.DoesNotContain("transientPrompt", written, StringComparison.Ordinal);
        Assert.False(BaseFile()["editor"]!["autosuggestions"]!.GetValue<bool>());
        Assert.Equal(999, BaseFile()["prompt"]!["gitTimeoutMs"]!.GetValue<int>());

        Assert.False(store.Current.Prompt.TransientPrompt);
        Assert.Equal("local-theme", store.Current.Theme);
        Assert.Equal(JsonConfigStore.LocalSource, store.GetSource("theme"));
        Assert.Equal(JsonConfigStore.BaseSource, store.GetSource("editor.autosuggestions"));
        Assert.Equal("default", store.GetSource("history.maxEntries"));

        using var reloaded = CreateStore();
        Assert.False(reloaded.Current.Editor.Autosuggestions);
        Assert.False(reloaded.Current.Prompt.TransientPrompt);
        Assert.Equal("local-theme", reloaded.Current.Theme);
    }

    [Fact]
    public void UpdateOfAnOverriddenValueDoesNotCopyTheLocalValue()
    {
        File.WriteAllText(_paths.LocalConfigFile, """{ "plugins": { "disabled": ["a"] } }""");
        using var store = CreateStore();
        store.Update(c => c.Theme = "nord");
        Assert.DoesNotContain("plugins", BaseFile().Select(p => p.Key));
        Assert.Equal("nord", BaseFile()["theme"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("editor.autosuggestions", "yes", "true")]
    [InlineData("editor.autosuggestions", "OFF", "false")]
    [InlineData("editor.completionMenuMaxRows", "15", "15")]
    [InlineData("editor.bellStyle", "VISUAL", "\"visual\"")]
    [InlineData("translation.disabled", "ls, cat", "[\"ls\",\"cat\"]")]
    [InlineData("translation.disabled", "[\"grep\"]", "[\"grep\"]")]
    [InlineData("terminal.fontSize", "13.5", "13.5")]
    [InlineData("terminal.fontFace", "null", "null")]
    [InlineData("theme", "\"quoted\"", "\"quoted\"")]
    [InlineData("keyBindings.Ctrl+K", "panel.git", "\"panel.git\"")]
    [InlineData("extensions.myplugin", "{\"color\": \"red\"}", "{\"color\":\"red\"}")]
    [InlineData("Editor.BellStyle", "audible", "\"audible\"")]
    public void SetValueConvertsToTheSettingsType(string path, string value, string expectedJson)
    {
        using var store = CreateStore();
        store.SetValue(path, value);
        Assert.Equal(expectedJson, store.GetValue(path) is { } v ? v.GetRawText().Replace(" ", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal).Replace("\r", string.Empty, StringComparison.Ordinal) : "null");
    }

    [Theory]
    [InlineData("editor.bellStyle", "loud", "expected one of: none, audible, visual")]
    [InlineData("editor.completionMenuMaxRows", "abc", "expected a whole number")]
    [InlineData("editor.completionMenuMaxRows", "0", "must be at least 1")]
    [InlineData("terminal.opacity", "150", "must be at most 100")]
    [InlineData("editor.autosuggestions", "maybe", "expected true or false")]
    [InlineData("editor.bellstyl", "none", "Did you mean 'editor.bellStyle'")]
    [InlineData("editor.autosuggestions.deeper", "1", "is a single value")]
    [InlineData("plugins.trustedAssemblies", "nothex", "does not match the expected format")]
    [InlineData("editor", "true", "is a group of settings")]
    public void InvalidValuesGetFriendlyErrors(string path, string value, string message)
    {
        using var store = CreateStore();
        var ex = Assert.Throws<ConfigValidationException>(() => store.SetValue(path, value));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(_paths.ConfigFile));
    }

    [Fact]
    public void InvalidValuesInTheFileFallBackToDefaultsAndAreReported()
    {
        File.WriteAllText(_paths.ConfigFile, """{ "editor": { "bellStyle": "loud", "completionMenuMaxRows": 5 }, "history": 3, "bogus": 1 }""");
        using var store = CreateStore();
        Assert.Equal("none", store.Current.Editor.BellStyle);
        Assert.Equal(5, store.Current.Editor.CompletionMenuMaxRows);
        Assert.Equal(50_000, store.Current.History.MaxEntries);
        Assert.Contains(store.Problems, p => p.Severity == ConfigProblemSeverity.Error && p.Path == "editor.bellStyle");
        Assert.Contains(store.Problems, p => p.Severity == ConfigProblemSeverity.Error && p.Path == "history");
        Assert.Contains(store.Problems, p => p.Severity == ConfigProblemSeverity.Warning && p.Path == "bogus");
    }

    [Fact]
    public void SyntaxErrorsAreReportedAndTheFileIsNeverOverwritten()
    {
        File.WriteAllText(_paths.ConfigFile, "{ \"theme\": ");
        using var store = CreateStore();
        Assert.Equal("pickle", store.Current.Theme);
        Assert.Contains(store.Problems, p => p.Message.Contains("Syntax error", StringComparison.Ordinal));
        Assert.Throws<ConfigValidationException>(() => store.SetValue("theme", "x"));
        store.Update(c => c.Theme = "y");
        Assert.Equal("{ \"theme\": ", File.ReadAllText(_paths.ConfigFile));
    }

    [Fact]
    public void ResetRemovesTheValueAndEmptySections()
    {
        using var store = CreateStore();
        store.SetValue("editor.bellStyle", "visual");
        Assert.True(store.Reset("editor.bellStyle"));
        Assert.Equal("none", store.Current.Editor.BellStyle);
        Assert.DoesNotContain("editor", BaseFile().Select(p => p.Key));
        Assert.False(store.Reset("editor.bellStyle"));
    }

    [Fact]
    public async Task ExternalEditsAreReloadedButOwnWritesAreNot()
    {
        using var store = CreateStore();
        store.SetValue("theme", "first");
        var events = 0;
        var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Changed += (_, e) =>
        {
            Interlocked.Increment(ref events);
            if (e.Config.Theme == "external")
            {
                reloaded.TrySetResult();
            }
        };
        store.StartWatching(debounceMs: 50);

        store.SetValue("theme", "own");
        await Task.Delay(400);
        Assert.Equal(1, Volatile.Read(ref events));

        File.WriteAllText(_paths.ConfigFile, """{ "theme": "external" }""");
        await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("external", store.Current.Theme);
    }

    [Fact]
    public void SchemaDescribesEverySettingWithMatchingDefaults()
    {
        var defaults = System.Text.Json.JsonSerializer.SerializeToNode(new PickleConfig(), PickleJson.Options)!.AsObject();
        foreach (var path in ConfigSchema.AllPaths())
        {
            var info = ConfigSchema.Resolve(path);
            Assert.True(info.Schema is not null, $"config.schema.json does not describe '{path}'. Add it to src/Pickle.Core/Config/Schemas/config.schema.json.");
            Assert.False(string.IsNullOrWhiteSpace(info.Schema!["description"]?.GetValue<string>()), $"'{path}' has no description in config.schema.json");
            if (info.Kind == ConfigNodeKind.Leaf)
            {
                var actual = JsonConfigStore.Find(defaults, info.Segments);
                Assert.True(JsonNode.DeepEquals(actual, info.Schema["default"]), $"Schema default for '{path}' is {info.Schema["default"]?.ToJsonString() ?? "null"} but the code default is {actual?.ToJsonString() ?? "null"}");
            }
        }
    }

    [Fact]
    public void EnsureSchemaReferenceWritesTheSchemaForEditors()
    {
        using var store = CreateStore();
        store.SetValue("theme", "x");
        var schemaFile = store.EnsureSchemaReference();
        Assert.True(File.Exists(schemaFile));
        Assert.Equal("./config.schema.json", BaseFile()["$schema"]!.GetValue<string>());
        Assert.Empty(store.Problems);
        Assert.Equal("x", store.Current.Theme);
    }
}
