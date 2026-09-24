using Pickle.Abstractions;
using Pickle.Testing;
using Pickle.Testing.Fakes;
using Pickle.Tui.Panels.Wizard;
using Pickle.Wizards;
using Terminal.Gui.App;
using Terminal.Gui.Time;
using Terminal.Gui.Views;

namespace Pickle.Tui.Tests.Wizard;

public sealed class WizardPanelTests : IDisposable
{
    private readonly TestPickle _pickle = TestPickle.Create();
    private readonly IApplication _app;

    public WizardPanelTests()
    {
        new WizardsPlugin().Initialize(_pickle.Runtime);
        _app = Application.Create(new VirtualTimeProvider());
        _app.Init();
        _app.StopAfterFirstIteration = true;
    }

    public void Dispose()
    {
        _app.Dispose();
        _pickle.Dispose();
    }

    [Fact]
    public void OpensWithTheTypedCommandParsedIntoTheForm()
    {
        var (panel, _) = Open("curl", "curl -X POST https://example.com -H 'A: b'");

        Assert.Equal("curl", panel.Definition?.Id);
        Assert.Equal("POST", panel.Values["method"]);
        Assert.Equal("https://example.com", panel.Values["url"]);
        Assert.Equal("A: b", panel.Values["headers"]);
        Assert.Equal("POST", panel.Editor("method")?.Text);
        Assert.Equal("https://example.com", panel.Editor("url")?.Text);
        Assert.Equal("curl -X POST -H 'A: b' https://example.com", panel.PreviewText);
    }

    [Fact]
    public void ChangingFieldsUpdatesThePreview()
    {
        var (panel, _) = Open("curl", "curl https://example.com");

        ((TextField)panel.Editor("userAgent")!).Text = "pickle/1.0";
        Assert.Equal("curl -A pickle/1.0 https://example.com", panel.PreviewText);

        ((CheckBox)panel.Editor("location")!).Value = CheckState.Checked;
        Assert.Equal("curl -L -A pickle/1.0 https://example.com", panel.PreviewText);

        panel.SetFieldValue("output", "my file.json");
        Assert.Contains("-o 'my file.json'", panel.PreviewText, StringComparison.Ordinal);

        panel.ExtraArgumentsField!.Text = "--ipv4";
        Assert.EndsWith("--ipv4 https://example.com", panel.PreviewText, StringComparison.Ordinal);
    }

    [Fact]
    public void RunReturnsTheCommand()
    {
        var (panel, context) = Open("curl", "curl https://example.com");
        panel.SetFieldValue("silent", "true");

        panel.RunCommand();

        Assert.Equal(new PanelResult(PanelResultKind.RunCommand, "curl -s https://example.com"), context.Result);
    }

    [Fact]
    public void InsertReplacesOnlyTheWizardCommandInThePipeline()
    {
        var (panel, context) = Open("curl", "curl -s https://x.test | ConvertFrom-Json");
        panel.SetFieldValue("location", "true");

        panel.InsertCommand();

        Assert.Equal(new PanelResult(PanelResultKind.ReplaceInput, "curl -s -L https://x.test | ConvertFrom-Json"), context.Result);
    }

    [Fact]
    public void ShowsValidationErrorsAndWarnings()
    {
        var (panel, _) = Open("robocopy", null);
        Assert.Contains("Source folder is required.", panel.Command!.Errors);
        Assert.Contains("Source folder is required", panel.MessagesText, StringComparison.Ordinal);

        panel.SetFieldValue("source", "C:\\a");
        panel.SetFieldValue("destination", "D:\\b");
        panel.SetFieldValue("mirror", "true");
        Assert.Empty(panel.Command!.Errors);
        Assert.Contains("⚠", panel.MessagesText, StringComparison.Ordinal);
    }

    [Fact]
    public void PresetsAndModes()
    {
        var (panel, _) = Open("git", null);
        panel.ApplyPreset(panel.Definition!.Presets.Single(p => p.Name == "Graph of all branches"));
        Assert.Equal("log", panel.ModeId);
        Assert.Equal("git log --oneline --graph --decorate --all -n 30", panel.PreviewText);

        panel.SelectMode("commit");
        panel.SetFieldValue("message", "hello");
        Assert.Equal("git commit -m hello", panel.PreviewText);

        panel.SelectMode("log");
        Assert.Equal("git log --oneline --graph --decorate --all -n 30", panel.PreviewText);
    }

    [Fact]
    public void TemplateFieldsBuildTheCompositeValue()
    {
        var (panel, _) = Open("ssh", "ssh user@host");
        panel.SetFieldValue("localForward.localPort", "8080");
        panel.SetFieldValue("localForward.remoteHost", "localhost");
        panel.SetFieldValue("localForward.remotePort", "80");
        Assert.Equal("ssh -L 8080:localhost:80 user@host", panel.PreviewText);
    }

    [Fact]
    public void PickerFiltersAndOpensAWizard()
    {
        var (panel, _) = Open(null, "Get-Date");
        Assert.Null(panel.Definition);
        Assert.Equal(_pickle.Runtime.Wizards.All.Count, panel.PickerMatches.Count);

        var matches = WizardPanel.Filter(_pickle.Runtime.Wizards.All, "dock");
        Assert.Equal("docker", matches[0].Id);
        Assert.Equal("kubectl", WizardPanel.Filter(_pickle.Runtime.Wizards.All, "kctl")[0].Id);

        panel.Pick(matches[0]);
        Assert.Equal("docker", panel.Definition?.Id);
        Assert.StartsWith("docker run", panel.PreviewText, StringComparison.Ordinal);
    }

    [Fact]
    public void SavesTheCommandAsAnAlias()
    {
        var (panel, _) = Open("curl", "curl -s https://api.test");

        Assert.False(panel.SaveAlias("bad name"));
        Assert.True(panel.SaveAlias("apiget"));

        var alias = _pickle.Runtime.Aliases.Get("apiget");
        Assert.Equal("curl -s https://api.test", alias?.Body);
        Assert.Equal(AliasKind.Simple, alias?.Kind);
    }

    [Fact]
    public void OffersInstallWhenTheToolIsMissing()
    {
        var winget = new FakeWingetService();
        _pickle.Runtime.Services.Add<Pickle.Abstractions.Services.IWingetService>(winget);

        Assert.True(Open("curl", null, toolExists: _ => false).Panel.InstallOffered);
        Assert.False(Open("curl", null, toolExists: _ => true).Panel.InstallOffered);
        Assert.False(Open("ssh", null, toolExists: _ => false).Panel.InstallOffered);
    }

    [Fact]
    public async Task F2OpensTheWizardForTheTypedCommand()
    {
        new WizardPanelPlugin().Initialize(_pickle.Runtime);
        Assert.NotNull(_pickle.Runtime.Panels.Get(WizardPanelPlugin.PanelId));
        var action = _pickle.Runtime.KeyBindings.GetAction(EditorActionNames.OpenWizard)!;

        var buffer = new FakeBuffer("Get-Date; curl.exe -s https://x.test");
        await action.Handler(buffer, CancellationToken.None);
        Assert.Equal([("wizard", (string?)"curl")], buffer.Panels);

        var unknown = new FakeBuffer("frobnicate --now");
        await action.Handler(unknown, CancellationToken.None);
        Assert.Equal([("wizard", (string?)null)], unknown.Panels);
    }

    private (WizardPanel Panel, PanelContext Context) Open(string? argument, string? input, Func<string, bool>? toolExists = null)
    {
        var context = new PanelContext { Pickle = _pickle.Runtime, Argument = argument, CurrentInput = input };
        var panel = new WizardPanel(context, toolExists ?? (_ => true));
        _app.Run(panel);
        return (panel, context);
    }

    private sealed class FakeBuffer(string text) : IEditorBuffer
    {
        public List<(string PanelId, string? Argument)> Panels { get; } = [];

        public string Text => text;

        public int Cursor => text.Length;

        public string? Suggestion => null;

        public void Insert(string value)
        {
        }

        public void Replace(string value, int cursor)
        {
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

        public void ShowPanel(string panelId, string? argument = null) => Panels.Add((panelId, argument));
    }
}
