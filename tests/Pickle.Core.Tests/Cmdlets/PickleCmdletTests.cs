using Pickle.Abstractions;
using Pickle.Testing;

namespace Pickle.Core.Tests.Cmdlets;

public class PickleCmdletTests
{
    [Fact]
    public async Task GetAndSetPickleConfig()
    {
        using var t = TestPickle.Create(start: true);
        t.Run("Set-PickleConfig -Path editor.bellStyle -Value visual");
        t.Run("Set-PickleConfig editor.autosuggestions $false");
        t.Run("Set-PickleConfig -Path translation.disabled -Value @('ls', 'cat')");
        t.Run("Set-PickleConfig -Path theme -Value here-only -Local");

        Assert.Equal(["visual"], t.Run("Get-PickleConfig -Path editor.bellStyle"));
        Assert.Equal(["False"], t.Run("Get-PickleConfig editor.autosuggestions"));
        Assert.Equal(["ls", "cat"], t.Run("Get-PickleConfig translation.disabled"));
        Assert.Equal(["here-only"], t.Run("(Get-PickleConfig).Theme"));
        Assert.Equal(["System.Boolean"], t.Run("(Get-PickleConfig -Path editor).SyntaxHighlighting.GetType().FullName"));
        Assert.Contains("here-only", File.ReadAllText(t.Paths.LocalConfigFile), StringComparison.Ordinal);
        Assert.DoesNotContain("here-only", File.ReadAllText(t.Paths.ConfigFile), StringComparison.Ordinal);

        var bad = await t.Runtime.Shell.InvokeAsync("Set-PickleConfig -Path editor.bellStyle -Value loud");
        Assert.Contains("expected one of: none, audible, visual", Assert.Single(bad.Errors).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PkConfigSubcommands()
    {
        using var t = TestPickle.Create(start: true);
        t.Run("pk config set editor.bellStyle audible");
        Assert.Equal(["audible"], t.Run("pk config get editor.bellStyle"));
        Assert.Contains("editor.bellStyle = \"audible\"", t.Terminal.RawOutput, StringComparison.Ordinal);

        t.Run("pk config set editor.bellStyle visual --local");
        t.Run("pk config set editor.bellStyle none");
        Assert.Contains("config.local.json overrides this", t.Terminal.RawOutput, StringComparison.Ordinal);
        Assert.Equal(["visual"], t.Run("pk config get editor.bellStyle"));

        t.Run("pk config reset editor.bellStyle --local");
        Assert.Equal(["none"], t.Run("pk config get editor.bellStyle"));
        Assert.Equal([t.Paths.ConfigFile], t.Run("pk config path"));
        Assert.Equal([t.Paths.LocalConfigFile], t.Run("pk config path --local"));
        Assert.Contains("\"bellStyle\"", t.Run("pk config schema")[0], StringComparison.Ordinal);
        Assert.Contains(t.Run("pk config list | ForEach-Object Path"), p => p == "sync.backend");

        t.Terminal.ClearRawOutput();
        t.Run("pk config");
        Assert.Contains("Your settings", t.Terminal.RawOutput, StringComparison.Ordinal);

        var bad = await t.Runtime.Shell.InvokeAsync("pk config set editor.bellstyl none");
        Assert.Contains("Did you mean 'editor.bellStyle'", Assert.Single(bad.Errors).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisterKeyBindingWithScriptBlockReplacesTheInput()
    {
        using var t = TestPickle.Create(start: true);
        t.Run("Register-PickleKeyBinding -Chord Alt+u -ScriptBlock { param($text) $text.ToUpperInvariant() }");
        var action = t.Runtime.KeyBindingRegistry.Bindings["Alt+U"];
        var info = t.Runtime.KeyBindingRegistry.GetAction(action)!;

        var buffer = new FakeBuffer("hello");
        await info.Handler(buffer, CancellationToken.None);
        Assert.Equal("HELLO", buffer.Text);

        t.Run("Register-PickleKeyBinding -Chord Ctrl+Alt+G -Action panel.git");
        Assert.Equal("panel.git", t.Runtime.KeyBindingRegistry.Bindings["Ctrl+Alt+G"]);

        var bad = await t.Runtime.Shell.InvokeAsync("Register-PickleKeyBinding -Chord 'Hyper+Q' -Action x");
        Assert.Contains("not a valid key chord", Assert.Single(bad.Errors).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RegisterWizardAcceptsHashtablesAndJson()
    {
        using var t = TestPickle.Create(start: true);
        t.Run("""
            Register-PickleWizard -Definition @{
                id = 'mytool'; title = 'My tool'; command = 'mytool'
                sections = @(@{ title = 'Main'; options = @(@{ id = 'verbose'; label = 'Verbose'; type = 'flag'; flag = '-v' }) })
            }
            """);
        var wizard = t.Runtime.WizardRegistry.Get("mytool")!;
        Assert.Equal(WizardOptionType.Flag, wizard.Sections[0].Options[0].Type);

        t.Run("""Register-PickleWizard -Definition '{ "id": "other", "command": "other" }'""");
        Assert.Equal("other", t.Runtime.WizardRegistry.Get("other")!.Title);
    }

    [Fact]
    public async Task RegisterCommandAndTranslationFromTheProfile()
    {
        using var t = TestPickle.Create(start: true);
        t.Run("Register-PickleCommand -Name greet -Description 'Greets' -ScriptBlock { param($who) \"hi $who\" }");
        Assert.Equal(["hi me"], t.Run("pk greet me"));

        var refused = await t.Runtime.Shell.InvokeAsync("Register-PickleCommand -Name config -Description x -ScriptBlock { 1 }");
        Assert.Contains("-Force", Assert.Single(refused.Errors).ToString(), StringComparison.Ordinal);

        t.Run("Register-PickleTranslation -Name tac -Description 'Reverse lines' -ScriptBlock { $input | Sort-Object -Descending }");
        Assert.Contains(t.Runtime.TranslationRegistry.Shims, s => s.Name == "tac" && s.FunctionBody.Contains("Sort-Object", StringComparison.Ordinal));

        t.Run("Register-PickleHook -Event Prompt -ScriptBlock { $global:promptSeen = $PickleEvent.Kind.ToString() }");
        await t.Runtime.Hooks.RaiseAsync(new HookEvent(HookKind.Prompt, Cwd: "/"));
        Assert.Equal(["Prompt"], t.Run("$global:promptSeen"));
    }

    private sealed class FakeBuffer(string text) : IEditorBuffer
    {
        public string Text { get; private set; } = text;

        public int Cursor { get; private set; } = text.Length;

        public string? Suggestion => null;

        public void Insert(string value) => Text += value;

        public void Replace(string value, int cursor)
        {
            Text = value;
            Cursor = cursor;
        }

        public void Accept()
        {
        }

        public void Redraw()
        {
        }

        public void OpenOverlay(IEditorOverlay overlay)
        {
        }

        public void CloseOverlay()
        {
        }

        public void ShowPanel(string panelId, string? argument = null)
        {
        }
    }
}
