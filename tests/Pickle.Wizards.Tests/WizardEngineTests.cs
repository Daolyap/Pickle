using Pickle.Abstractions;

namespace Pickle.Wizards.Tests;

public class WizardEngineTests
{
    private static readonly WizardDefinition Tool = WizardLoader.Parse("""
        {
          "id": "tool", "title": "Tool", "command": "tool",
          "sections": [{
            "title": "All",
            "options": [
              { "id": "silent", "label": "Silent", "type": "flag", "flag": "-s", "flagAliases": ["--silent"] },
              { "id": "showError", "label": "Show error", "type": "flag", "flag": "-S" },
              { "id": "location", "label": "Location", "type": "flag", "flag": "-L" },
              { "id": "output", "label": "Output", "type": "path", "flag": "-o", "flagAliases": ["--output"] },
              { "id": "out", "label": "Out", "type": "text", "flag": "--out", "valueStyle": "equals" },
              { "id": "level", "label": "Level", "type": "text", "flag": "-O", "valueStyle": "none" },
              { "id": "method", "label": "Method", "type": "choice", "flag": "-X", "flagAliases": ["--request"], "default": "GET",
                "choices": [ { "value": "GET" }, { "value": "POST" } ] },
              { "id": "headers", "label": "Headers", "type": "keyValueList", "flag": "-H", "keyValueSeparator": ": " },
              { "id": "env", "label": "Env", "type": "keyValueList", "flag": "-e", "keyValueSeparator": "=" },
              { "id": "data", "label": "Data", "type": "list", "flag": "-d" },
              { "id": "retry", "label": "Retry", "type": "number", "flag": "--retry" },
              { "id": "retryDelay", "label": "Retry delay", "type": "number", "flag": "--retry-delay", "dependsOn": "retry" },
              { "id": "gzip", "label": "Gzip", "type": "flag", "flag": "--gzip", "dependsOn": "method=POST" },
              { "id": "plain", "label": "Plain", "type": "flag", "flag": "--plain", "dependsOn": "!silent" },
              { "id": "code", "label": "Code", "type": "text", "flag": "--code", "validation": "^[A-Z]{3}$" },
              { "id": "force", "label": "Force", "type": "flag", "flag": "--force", "warning": "Overwrites everything." },
              { "id": "forward", "label": "Forward", "type": "text", "flag": "-L2", "template": "[{bind}:]{local}:{host}:{remote}" },
              { "id": "url", "label": "URL", "type": "positional", "position": 1, "required": true },
              { "id": "rest", "label": "Rest", "type": "list", "position": 2 }
            ]
          }]
        }
        """);

    private static Dictionary<string, string> V(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    private static string Line(params (string Key, string Value)[] pairs) => WizardEngine.Build(Tool, null, V(pairs)).CommandLine;

    [Fact]
    public void ValueStyles()
    {
        Assert.Equal("tool -o 'my file.txt' --out=x.txt -O9 u", Line(("output", "my file.txt"), ("out", "x.txt"), ("level", "9"), ("url", "u")));
        Assert.Equal("tool '--out=a b' '-Oa b' u", Line(("out", "a b"), ("level", "a b"), ("url", "u")));
    }

    [Fact]
    public void DefaultsAreNotEmitted()
    {
        Assert.Equal("tool u", Line(("method", "GET"), ("url", "u")));
        Assert.Equal("tool -X POST u", Line(("method", "POST"), ("url", "u")));
    }

    [Fact]
    public void KeyValueListsAndListsRepeatTheFlag()
    {
        Assert.Equal(
            "tool -H 'Accept: application/json' -H 'X-Id: 7' -e A=1 -e 'B= x' -d a=1 -d b=2 u",
            Line(("headers", "Accept:application/json\n\n  X-Id:   7"), ("env", "A=1\nB= x"), ("data", "a=1\nb=2"), ("url", "u")));
    }

    [Fact]
    public void TemplatesComposeSubValues()
    {
        Assert.Equal("tool -L2 8080:db:5432 u", Line(("forward.local", "8080"), ("forward.host", "db"), ("forward.remote", "5432"), ("url", "u")));
        Assert.Equal(
            "tool -L2 0.0.0.0:8080:db:5432 u",
            Line(("forward.bind", "0.0.0.0"), ("forward.local", "8080"), ("forward.host", "db"), ("forward.remote", "5432"), ("url", "u")));

        var partial = WizardEngine.Build(Tool, null, V(("forward.local", "8080"), ("url", "u")));
        Assert.Contains(partial.Errors, e => e.Contains("Host", StringComparison.Ordinal) && e.Contains("Remote", StringComparison.Ordinal));
        Assert.Equal("tool u", partial.CommandLine);
    }

    [Fact]
    public void DependsOnGatesOptions()
    {
        Assert.Equal("tool u", Line(("retryDelay", "2"), ("url", "u")));
        Assert.Equal("tool --retry 3 --retry-delay 2 u", Line(("retry", "3"), ("retryDelay", "2"), ("url", "u")));
        Assert.Equal("tool u", Line(("gzip", "true"), ("url", "u")));
        Assert.Equal("tool -X POST --gzip u", Line(("method", "POST"), ("gzip", "true"), ("url", "u")));
        Assert.Equal("tool --plain u", Line(("plain", "true"), ("url", "u")));
        Assert.Equal("tool -s u", Line(("silent", "true"), ("plain", "true"), ("url", "u")));
    }

    [Fact]
    public void RequiredAndValidationErrors()
    {
        var command = WizardEngine.Build(Tool, null, V(("code", "abc"), ("retry", "many")));
        Assert.Contains("URL is required.", command.Errors);
        Assert.Contains(command.Errors, e => e.StartsWith("Code:", StringComparison.Ordinal));
        Assert.Contains(command.Errors, e => e.StartsWith("Retry:", StringComparison.Ordinal) && e.Contains("number", StringComparison.Ordinal));
        Assert.False(command.IsValid);

        var fields = WizardEngine.ValidateFields(Tool, null, V(("code", "abc")));
        Assert.True(fields.ContainsKey("code"));
        Assert.True(fields.ContainsKey("url"));
        Assert.Empty(WizardEngine.Build(Tool, null, V(("code", "ABC"), ("url", "u"))).Errors);
    }

    [Fact]
    public void WarningsForDangerousOptions()
    {
        var command = WizardEngine.Build(Tool, null, V(("force", "true"), ("url", "u")));
        Assert.Equal(["Force: Overwrites everything."], command.Warnings);
    }

    [Theory]
    [InlineData("it's")]
    [InlineData("two words")]
    [InlineData("$env:SECRET")]
    [InlineData("$(Remove-Item x)")]
    [InlineData("a`b")]
    [InlineData("say \"hi\"")]
    [InlineData("‘smart’ and ’quotes‛")]
    [InlineData("a;b|c&d")]
    [InlineData("@splat")]
    [InlineData("-looks-like-a-flag")]
    [InlineData("–en-dash")]
    [InlineData("1kb")]
    [InlineData("0x10")]
    [InlineData("*.txt")]
    [InlineData("a,b")]
    [InlineData("#comment")]
    [InlineData("{script}")]
    [InlineData("stash@{0}")]
    [InlineData("--%")]
    [InlineData("line1\nline2")]
    [InlineData("")]
    public void QuotedValuesReachTheProgramUnchanged(string value)
    {
        var line = "tool " + PowerShellQuoting.FormatArgument(value);
        var parsed = CommandTokenizer.ParseFirst(line)!;
        var argument = Assert.Single(parsed.Arguments);
        Assert.True(argument.IsLiteral, line);
        Assert.Equal(value, argument.Value);
    }

    [Theory]
    [InlineData("simple")]
    [InlineData("C:\\Users\\me\\file.txt")]
    [InlineData("user@host:/path")]
    [InlineData("8080:localhost:80")]
    [InlineData("scale=1280:-2")]
    [InlineData("23")]
    [InlineData("~/.ssh/id_ed25519")]
    public void SafeValuesStayBare(string value) => Assert.Equal(value, PowerShellQuoting.FormatArgument(value));

    [Fact]
    public void FieldValuesCannotInjectCommands()
    {
        var command = WizardEngine.Build(Tool, null, V(("output", "x'; Remove-Item -Recurse C:\\ ; '"), ("url", "u")));
        var parsed = CommandTokenizer.ParseCommands(command.CommandLine);
        var single = Assert.Single(parsed);
        Assert.Equal("tool", single.Name);
        Assert.Contains(single.Arguments, a => a.Value == "x'; Remove-Item -Recurse C:\\ ; '");
    }

    [Fact]
    public void ParsesCommonFlagSpellings()
    {
        var (_, values, unknown) = WizardEngine.Parse(Tool, "tool -sSL --request=POST -ofile.txt -H 'A:b' -H \"C: d\" -XPOST https://x.test");
        Assert.Empty(unknown.Where(u => u != "-XPOST"));
        Assert.Equal("true", values["silent"]);
        Assert.Equal("true", values["showError"]);
        Assert.Equal("true", values["location"]);
        Assert.Equal("POST", values["method"]);
        Assert.Equal("file.txt", values["output"]);
        Assert.Equal("A: b\nC: d", values["headers"]);
        Assert.Equal("https://x.test", values["url"]);
    }

    [Fact]
    public void ParsesCombinedFlagsEndingInAValue()
    {
        var (_, values, unknown) = WizardEngine.Parse(Tool, "tool -sSo out.txt -Lsomething u");
        Assert.Empty(unknown);
        Assert.Equal("out.txt", values["output"]);
        Assert.Equal("true", values["location"]);
        Assert.Equal("true", values["silent"]);
        Assert.Equal("u", values["url"]);
        Assert.Equal("something", values["rest"]);
    }

    [Fact]
    public void ParsesEqualsNoneAndPowerShellColonForms()
    {
        var (_, values, unknown) = WizardEngine.Parse(Tool, "tool --out=a.txt -O7 --output:b.txt -X:POST u");
        Assert.Empty(unknown);
        Assert.Equal("a.txt", values["out"]);
        Assert.Equal("7", values["level"]);
        Assert.Equal("b.txt", values["output"]);
        Assert.Equal("POST", values["method"]);
    }

    [Fact]
    public void RepeatedSingleValueFlagsAndUnknownsArePreserved()
    {
        var result = WizardEngine.Parse(Tool, "tool -o a.txt -o b.txt --mystery $HOME u");
        Assert.Equal("a.txt", result.Values["output"]);
        Assert.Equal(["-o b.txt", "--mystery", "$HOME"], result.UnknownTokens);

        var rebuilt = WizardEngine.Build(Tool, result.ModeId, result.Values, result.UnknownTokens);
        Assert.Equal("tool -o a.txt -o b.txt --mystery $HOME u", rebuilt.CommandLine);
        Assert.Empty(rebuilt.Errors);
    }

    [Fact]
    public void InvalidExtraArgumentsAreQuotedNotExecuted()
    {
        var command = WizardEngine.Build(Tool, null, V(("url", "u")), ["x; Remove-Item y"]);
        Assert.NotEmpty(command.Errors);
        Assert.Single(CommandTokenizer.ParseCommands(command.CommandLine));
    }

    [Fact]
    public void ParseKeepsDependentOptionsWithoutTheirDependencyAsUnknown()
    {
        var result = WizardEngine.Parse(Tool, "tool --retry-delay 5 u");
        Assert.False(result.Values.ContainsKey("retryDelay"));
        Assert.Equal(["--retry-delay 5"], result.UnknownTokens);
    }

    [Fact]
    public void ParseFindsTheCommandInsideAPipeline()
    {
        const string input = "Get-Date; tool -s u | Out-File x.txt";
        var result = WizardEngine.Parse(Tool, input);
        Assert.True(result.Matched);
        Assert.Equal("tool -s u", input.Substring(result.Start, result.Length));
        Assert.False(WizardEngine.Parse(Tool, "other -s").Matched);
    }

    [Fact]
    public void ParsesTemplates()
    {
        var (_, values, unknown) = WizardEngine.Parse(Tool, "tool -L2 127.0.0.1:8080:db:5432 u");
        Assert.Empty(unknown);
        Assert.Equal("127.0.0.1", values["forward.bind"]);
        Assert.Equal("8080", values["forward.local"]);
        Assert.Equal("db", values["forward.host"]);
        Assert.Equal("5432", values["forward.remote"]);
    }

    private static readonly WizardDefinition Netsh = WizardLoader.Parse("""
        {
          "id": "ns", "title": "ns", "command": "netsh",
          "modes": [{
            "id": "add", "title": "add", "subcommand": ["interface", "portproxy", "add", "v4tov4"],
            "sections": [{ "title": "x", "options": [
              { "id": "listenport", "label": "Listen port", "type": "number", "flag": "listenport", "valueStyle": "equals" },
              { "id": "name", "label": "Name", "type": "text", "flag": "name", "valueStyle": "equals" },
              { "id": "verbose", "label": "Verbose", "type": "flag", "flag": "verbose" }
            ]}]
          }]
        }
        """);

    [Fact]
    public void DashlessKeyValueFlags()
    {
        var command = WizardEngine.Build(Netsh, "add", V(("listenport", "8080"), ("name", "My Rule"), ("verbose", "true")));
        Assert.Equal("netsh interface portproxy add v4tov4 listenport=8080 'name=My Rule' verbose", command.CommandLine);

        var (mode, values, unknown) = WizardEngine.Parse(Netsh, "netsh interface portproxy add v4tov4 LISTENPORT=8080 name=\"My Rule\" Verbose");
        Assert.Equal("add", mode);
        Assert.Empty(unknown);
        Assert.Equal("8080", values["listenport"]);
        Assert.Equal("My Rule", values["name"]);
        Assert.Equal("true", values["verbose"]);
    }

    private static readonly WizardDefinition Events = WizardLoader.Parse("""
        {
          "id": "ev", "title": "ev", "command": "Get-WinEvent",
          "sections": [{ "title": "x", "options": [
            { "id": "filter", "label": "Filter", "type": "text", "flag": "-FilterHashtable", "raw": true,
              "template": "@{LogName={logName}[; Id={id}][; StartTime=(Get-Date).AddHours(-{hours})]}" },
            { "id": "start", "label": "Start", "type": "text", "flag": "-Start", "raw": true },
            { "id": "max", "label": "Max", "type": "number", "flag": "-MaxEvents" }
          ]}]
        }
        """);

    [Fact]
    public void RawTemplatesQuoteSubValuesAsPowerShellLiterals()
    {
        var command = WizardEngine.Build(Events, null, V(("filter.logName", "O'Brien's log"), ("filter.id", "4624, 4625"), ("filter.hours", "24"), ("max", "5")));
        Assert.Equal("Get-WinEvent -FilterHashtable @{LogName='O''Brien''s log'; Id=4624,4625; StartTime=(Get-Date).AddHours(-24)} -MaxEvents 5", command.CommandLine);
        Assert.Empty(command.Errors);

        var (_, values, unknown) = WizardEngine.Parse(Events, "get-winevent -filterhashtable @{ LogName = 'System' ; Id = 41 } -maxevents:3");
        Assert.Empty(unknown);
        Assert.Equal("System", values["filter.logName"]);
        Assert.Equal("41", values["filter.id"]);
        Assert.Equal("3", values["max"]);
    }

    [Fact]
    public void RawValuesMustBeASingleExpression()
    {
        Assert.Equal("Get-WinEvent -Start (Get-Date).AddDays(-1)", WizardEngine.Build(Events, null, V(("start", "(Get-Date).AddDays(-1)"))).CommandLine);

        var bad = WizardEngine.Build(Events, null, V(("start", "1; Remove-Item x")));
        Assert.NotEmpty(bad.Errors);
        Assert.Single(CommandTokenizer.ParseCommands(bad.CommandLine));
    }

    private static WizardDefinition Builtin(string id) => WizardLoader.LoadEmbedded($"Pickle.Wizards.Definitions.{id}.json");

    [Fact]
    public void RawPositionalTakesTheRestOfTheLine()
    {
        var docker = Builtin("docker");
        var (mode, values, unknown) = WizardEngine.Parse(docker, "docker run -it --rm -e A=1 ubuntu bash -c 'echo $HOME' --not-a-docker-flag");
        Assert.Equal("run", mode);
        Assert.Empty(unknown);
        Assert.Equal("ubuntu", values["image"]);
        Assert.Equal("bash -c 'echo $HOME' --not-a-docker-flag", values["command"]);
        Assert.Equal("true", values["interactive"]);
        Assert.Equal("true", values["tty"]);

        var kubectl = Builtin("kubectl");
        var exec = WizardEngine.Parse(kubectl, "kubectl exec -it web-0 -n prod -- sh -c ls");
        Assert.Equal("exec", exec.ModeId);
        Assert.Equal("sh -c ls", exec.Values["command"]);
        Assert.Equal("prod", exec.Values["namespace"]);
        Assert.Equal("kubectl -n prod exec -i -t web-0 -- sh -c ls", WizardEngine.Build(kubectl, exec.ModeId, exec.Values).CommandLine);
    }

    [Fact]
    public void ModesAndGlobalOptions()
    {
        var git = Builtin("git");
        var (mode, values, unknown) = WizardEngine.Parse(git, "git -C repo log --oneline -n 5 -- src/a.cs src/b.cs");
        Assert.Equal("log", mode);
        Assert.Empty(unknown);
        Assert.Equal("repo", values["directory"]);
        Assert.Equal("5", values["maxCount"]);
        Assert.Equal("src/a.cs\nsrc/b.cs", values["paths"]);

        var switchMode = WizardEngine.Parse(git, "git switch -c feature");
        Assert.Equal("switch", switchMode.ModeId);
        Assert.Equal("true", switchMode.Values["create"]);

        var commit = WizardEngine.Parse(git, "git commit -am 'Fix it'");
        Assert.Equal("true", commit.Values["all"]);
        Assert.Equal("Fix it", commit.Values["message"]);

        var reset = WizardEngine.Build(git, "reset", V(("mode", "--hard")));
        Assert.Equal("git reset --hard", reset.CommandLine);
        Assert.Single(reset.Warnings);

        Assert.Null(WizardEngine.Parse(git, "git push origin main").ModeId);
    }

    [Fact]
    public void LeadingPositionalsComeBeforeOptions()
    {
        var robocopy = Builtin("robocopy");
        var command = WizardEngine.Build(robocopy, null, V(("source", "C:\\a b"), ("destination", "D:\\c"), ("mirror", "true"), ("retries", "2")));
        Assert.Equal("robocopy 'C:\\a b' D:\\c /MIR /R:2", command.CommandLine);
        Assert.Contains(command.Warnings, w => w.Contains("Deletes", StringComparison.Ordinal));

        var parsed = WizardEngine.Parse(robocopy, "robocopy C:\\a D:\\c *.txt /mir /r:3 /XD node_modules");
        Assert.Empty(parsed.UnknownTokens);
        Assert.Equal("*.txt", parsed.Values["files"]);
        Assert.Equal("3", parsed.Values["retries"]);
        Assert.Equal("node_modules", parsed.Values["excludeDirs"]);
    }

    [Fact]
    public void TarBundlesFlagChoices()
    {
        var tar = Builtin("tar");
        var (_, values, unknown) = WizardEngine.Parse(tar, "tar -xzvf archive.tar.gz -C out");
        Assert.Empty(unknown);
        Assert.Equal("-x", values["operation"]);
        Assert.Equal("-z", values["compression"]);
        Assert.Equal("true", values["verbose"]);
        Assert.Equal("archive.tar.gz", values["file"]);
        Assert.Equal("out", values["directory"]);
    }

    [Fact]
    public void SingleDashLongOptionToolsDontSplitFlags()
    {
        var ffmpeg = Builtin("ffmpeg");
        var (_, values, unknown) = WizardEngine.Parse(ffmpeg, "ffmpeg -i in.mp4 -itsoffset 1 -c:v libx264 -crf 20 out.mp4");
        Assert.Equal("in.mp4", values["inputs"]);
        Assert.Equal("libx264", values["videoCodec"]);
        Assert.Equal("20", values["crf"]);
        Assert.Equal("out.mp4", values["output"]);
        Assert.Contains("-itsoffset", unknown);
    }
}
