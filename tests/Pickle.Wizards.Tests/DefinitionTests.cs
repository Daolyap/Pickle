using System.Text;
using Pickle.Abstractions;
using Pickle.Testing;

namespace Pickle.Wizards.Tests;

public class DefinitionTests
{
    private static readonly IReadOnlyList<WizardDefinition> All = WizardLoader.LoadEmbedded((name, ex) => throw new InvalidOperationException(name, ex));

    public static TheoryData<string> Ids()
    {
        var data = new TheoryData<string>();
        foreach (var definition in All)
        {
            data.Add(definition.Id);
        }

        return data;
    }

    public static TheoryData<string, string> Presets()
    {
        var data = new TheoryData<string, string>();
        foreach (var definition in All)
        {
            foreach (var preset in definition.Presets)
            {
                data.Add(definition.Id, preset.Name);
            }
        }

        return data;
    }

    private static WizardDefinition Get(string id) => All.Single(d => d.Id == id);

    [Fact]
    public void AllExpectedWizardsAreEmbedded()
    {
        string[] expected =
        [
            "7z", "adb", "certutil", "curl", "docker", "ffmpeg", "get-winevent", "git", "kubectl", "netsh", "nmap",
            "openssl", "robocopy", "rsync", "scp", "ssh", "tar", "yt-dlp",
        ];
        Assert.Equal(expected, All.Select(d => d.Id).Order(StringComparer.Ordinal));
        Assert.Equal(All.Count, WizardLoader.EmbeddedNames.Count);
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void DefinitionIsValid(string id)
    {
        var definition = Get(id);
        Assert.Empty(WizardValidator.Validate(definition));
        Assert.NotEmpty(definition.Presets);
        Assert.False(string.IsNullOrWhiteSpace(definition.Description));
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void PresetBuildsWithoutErrors(string id, string presetName)
    {
        var definition = Get(id);
        var preset = definition.Presets.Single(p => p.Name == presetName);
        var command = WizardEngine.Build(definition, preset.Mode, preset.Values);
        Assert.Empty(command.Errors);
        Assert.True(PowerShellQuoting.IsArgumentList(command.CommandLine[definition.Command.Length..]), command.CommandLine);
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void PresetRoundTrips(string id, string presetName)
    {
        var definition = Get(id);
        var preset = definition.Presets.Single(p => p.Name == presetName);
        var built = WizardEngine.Build(definition, preset.Mode, preset.Values);

        var (modeId, values, unknown) = WizardEngine.Parse(definition, built.CommandLine);

        Assert.Equal(WizardSchema.ResolveMode(definition, preset.Mode)?.Id, modeId);
        Assert.Empty(unknown);
        Assert.Equal(
            WizardEngine.Normalize(definition, preset.Mode, preset.Values).OrderBy(kv => kv.Key, StringComparer.Ordinal),
            values.OrderBy(kv => kv.Key, StringComparer.Ordinal));
        Assert.Equal(built.CommandLine, WizardEngine.Build(definition, modeId, values).CommandLine);
    }

    [Fact]
    public void PresetCommandLines()
    {
        var sb = new StringBuilder();
        foreach (var definition in All.OrderBy(d => d.Id, StringComparer.Ordinal))
        {
            sb.Append("## ").Append(definition.Id).Append('\n');
            foreach (var preset in definition.Presets)
            {
                var command = WizardEngine.Build(definition, preset.Mode, preset.Values);
                sb.Append(preset.Name).Append(":\n  ").Append(command.CommandLine).Append('\n');
                foreach (var warning in command.Warnings)
                {
                    sb.Append("  ! ").Append(warning).Append('\n');
                }
            }

            sb.Append('\n');
        }

        Snapshot.Match(sb.ToString());
    }

    [Fact]
    public void DangerousOptionsCarryWarnings()
    {
        Assert.NotNull(Get("robocopy").Sections.SelectMany(s => s.Options).Single(o => o.Flag == "/MIR").GetWarning());
        Assert.NotNull(Get("rsync").Sections.SelectMany(s => s.Options).Single(o => o.Flag == "--delete").GetWarning());
        var reset = Get("git").Modes.Single(m => m.Id == "reset").Sections[0].Options.Single(o => o.Id == "mode");
        Assert.NotNull(reset.Choices.Single(c => c.Value == "--hard").GetWarning());
    }

    [Fact]
    public void WindowsOnlyToolsAreMarked()
    {
        Assert.All(new[] { "robocopy", "get-winevent", "netsh", "certutil" }, id => Assert.True(Get(id).WindowsOnly, id));
        Assert.False(Get("curl").WindowsOnly);
    }

    [Fact]
    public void NmapDescriptionMentionsAuthorization() =>
        Assert.Contains("authorized", Get("nmap").Description, StringComparison.OrdinalIgnoreCase);
}
