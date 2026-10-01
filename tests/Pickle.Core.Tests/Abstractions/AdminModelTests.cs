using Pickle.Abstractions.Services;

namespace Pickle.Core.Tests.Abstractions;

public class AdminModelTests
{
    private const string WindowsHosts = "# Copyright (c) 1993-2009 Microsoft Corp.\r\n#\r\n# localhost name resolution is handled within DNS itself.\r\n#\t127.0.0.1       localhost\r\n#\t::1             localhost\r\n\r\n10.0.0.5  web web.lan  # dev box\r\n";

    [Fact]
    public void HostsDocumentRoundTripsUntouchedLinesByteForByte()
    {
        var document = HostsDocument.Parse(WindowsHosts);

        Assert.Equal(WindowsHosts, document.Serialize());
        Assert.True(document.UsesCrLf);
        var web = Assert.Single(document.Entries, e => e.Enabled);
        Assert.Equal(("10.0.0.5", "dev box"), (web.Address, web.Comment));
        Assert.Equal(["web", "web.lan"], web.Names);
        Assert.Equal(2, document.Entries.Count(e => !e.Enabled));
    }

    [Fact]
    public void HostsEditsOnlyTouchTheirOwnLines()
    {
        var document = HostsDocument.Parse(WindowsHosts);

        document.Add(new HostsEntry("127.0.0.1", ["blocked.example"], null, true));
        var index = document.Entries.ToList().FindIndex(e => e.Names.Contains("web"));
        document.SetEnabled(index, false);

        var text = document.Serialize();
        Assert.StartsWith(WindowsHosts[..WindowsHosts.IndexOf("10.0.0.5", StringComparison.Ordinal)], text, StringComparison.Ordinal);
        Assert.Contains("# 10.0.0.5        web web.lan  # dev box\r\n", text, StringComparison.Ordinal);
        Assert.EndsWith("127.0.0.1       blocked.example\r\n", text, StringComparison.Ordinal);
        HostsDocument.Validate(text);
    }

    [Theory]
    [InlineData("not an entry")]
    [InlineData("10.0.0.5")]
    [InlineData("10.0.0.256 web")]
    [InlineData("10.0.0.5 we b$")]
    [InlineData("10.0.0.5 -bad")]
    [InlineData("fe80::1%eth0 router")]
    public void HostsValidationRejectsMalformedLines(string line) =>
        Assert.Throws<ArgumentException>(() => HostsDocument.Validate("# ok\n" + line + "\n"));

    [Fact]
    public void HostsValidationRejectsControlCharactersAndHugeFiles()
    {
        Assert.Throws<ArgumentException>(() => HostsDocument.Validate("127.0.0.1 a\0b\n"));
        Assert.Throws<ArgumentException>(() => HostsDocument.Validate(new string('#', HostsDocument.MaxBytes + 1)));
        HostsDocument.Validate("::1 localhost\n127.0.0.1\tlocalhost kubernetes.docker.internal\n");
    }

    [Fact]
    public void DuplicateNamesAreReportedPerAddressFamily()
    {
        var document = HostsDocument.Parse("127.0.0.1 a b\n127.0.0.2 b\n::1 b\n");

        Assert.Equal(["b"], document.Duplicates());
    }

    [Fact]
    public void PathListEditsAndFlagsProblems()
    {
        var sep = PathList.Separator;
        // "/usr/bin" is not a full path on Windows (no drive), so build the folders from the platform's root.
        var root = Path.GetPathRoot(Path.GetFullPath(Path.DirectorySeparatorChar.ToString()))!;
        var bin = Path.Combine(root, "usr", "bin");
        var nope = Path.Combine(root, "nope");
        var tool = Path.Combine(root, "opt", "tool");
        var list = PathList.Parse($"{bin}{sep}{nope}{sep}{bin}{sep}relative", sep);

        Assert.Equal([bin, nope, bin, "relative"], list.Items);
        Func<string, bool> exists = d => d == bin;
        Assert.Null(list.Problem(0, exists, x => x));
        Assert.Equal("missing", list.Problem(1, exists, x => x));
        Assert.Equal("duplicate", list.Problem(2, exists, x => x));
        Assert.Equal("relative", list.Problem(3, exists, x => x));

        Assert.True(list.Move(1, -1));
        Assert.Equal(nope, list.Items[0]);
        Assert.False(list.Move(0, -1));
        list.RemoveAt(0);
        list.Add(tool, 0);
        Assert.Equal(tool, list.Items[0]);
        Assert.Throws<ArgumentException>(() => list.Add("a" + sep + "b"));
        Assert.Throws<ArgumentException>(() => list.Add(" "));
    }

    [Fact]
    public void EnvironmentRulesProtectHijackableVariablesAndBadNames()
    {
        EnvironmentRules.Validate("EDITOR", "vim", machineScope: true);
        Assert.Throws<ArgumentException>(() => EnvironmentRules.Validate("ComSpec", "x", machineScope: true));
        EnvironmentRules.Validate("ComSpec", "x", machineScope: false);
        Assert.Throws<ArgumentException>(() => EnvironmentRules.Validate("1BAD", "x", false));
        Assert.Throws<ArgumentException>(() => EnvironmentRules.Validate("A=B", "x", false));
        Assert.Throws<ArgumentException>(() => EnvironmentRules.Validate("A", "line\nbreak", false));
    }

    [Fact]
    public void DotEnvKeepsCommentsAndQuotesValuesThatNeedIt()
    {
        var document = DotEnvDocument.Parse("# secrets\nexport API_URL=https://x.test/a # trailing\nNAME=\"two words\"\nEMPTY=\n");

        Assert.Equal([("API_URL", "https://x.test/a"), ("NAME", "two words"), ("EMPTY", string.Empty)], document.Variables);

        document.Set("NAME", "it's \"q\"");
        document.Set("NEW", "plain");
        document.Remove("EMPTY");

        Assert.Equal("# secrets\nexport API_URL=https://x.test/a # trailing\nNAME=\"it's \\\"q\\\"\"\nNEW=plain\n", document.Serialize());
        Assert.Equal("it's \"q\"", DotEnvDocument.Parse(document.Serialize()).Variables.Single(v => v.Name == "NAME").Value);
        Assert.Throws<ArgumentException>(() => document.Set("BAD NAME", "x"));
        Assert.Throws<ArgumentException>(() => document.Set("A", "a\nb"));
    }
}
