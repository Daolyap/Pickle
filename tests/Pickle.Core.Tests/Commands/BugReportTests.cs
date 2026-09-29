using System.IO.Compression;
using Pickle.Core.Commands;
using Pickle.Testing;

namespace Pickle.Core.Tests.Commands;

public class BugReportTests
{
    [Fact]
    public void JsonSecretsAreMaskedByKeyAndUrlCredentialsRemoved()
    {
        var json = """
            { // comment
              "theme": "nord",
              "sync": { "remote": "https://me:ghp_abc123@github.com/me/dots.git", "githubToken": "ghp_secret", "api-key": 42 },
              "extensions": { "x": { "Password": "hunter2", "list": ["https://u:p@host/x", "plain"] } },
            }
            """;
        var redacted = BugReportCommand.RedactJson(json);

        Assert.Contains("\"theme\": \"nord\"", redacted, StringComparison.Ordinal);
        Assert.Contains("https://***@github.com/me/dots.git", redacted, StringComparison.Ordinal);
        Assert.Contains("https://***@host/x", redacted, StringComparison.Ordinal);
        foreach (var secret in new[] { "ghp_abc123", "ghp_secret", "hunter2", "42" })
        {
            Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("clone https://user:pw@example.com/repo", "clone https://***@example.com/repo")]
    [InlineData("Authorization: Bearer abcdefghijklmnop", "Authorization: Bearer ***")]
    [InlineData("connect password=hunter2; timeout=5", "connect password=***; timeout=5")]
    [InlineData("api_key: sk-123456", "api_key: ***")]
    [InlineData("nothing to see here", "nothing to see here")]
    public void TextSecretsAreRemoved(string input, string expected) => Assert.Equal(expected, BugReportCommand.RedactText(input));

    [Fact]
    public void HomeAndUserNameAreReplacedButNotInsideWords()
    {
        var scrub = BugReportCommand.Scrubber(@"C:\Users\alice", "alice");
        Assert.Equal(@"~\src and ~/x by <user>, not malice", scrub(@"C:\Users\alice\src and C:/Users/alice/x by Alice, not malice"));
    }

    [Fact]
    public void WritesAZipWithTheReportConfigAndLogsButNoHistory()
    {
        using var t = TestPickle.Create(start: true);
        File.WriteAllText(t.Paths.ConfigFile, """{ "theme": "pickle", "sync": { "remote": "https://bob:sekrit@git.example/x.git" } }""");
        File.WriteAllText(t.Paths.HistoryFile, "{\"text\":\"echo private-history\"}\n");
        Directory.CreateDirectory(t.Paths.LogDir);
        File.WriteAllText(Path.Combine(t.Paths.LogDir, "pickle-20260101.log"), "INFO started\nWARN token=abcdef123456 failed\n");
        var zip = Path.Combine(Directory.CreateTempSubdirectory("pickle-report").FullName, "report.zip");

        t.Run($"pk bugreport --out '{zip}'");

        Assert.Contains("Bug report written to", t.Terminal.GetScreenText(), StringComparison.Ordinal);
        using var archive = ZipFile.OpenRead(zip);
        var names = archive.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("report.md", names);
        Assert.Contains("config.json", names);
        Assert.Contains("logs/pickle-20260101.log", names);
        Assert.DoesNotContain(names, n => n.Contains("history", StringComparison.OrdinalIgnoreCase));

        string Read(string name)
        {
            using var reader = new StreamReader(archive.GetEntry(name)!.Open());
            return reader.ReadToEnd();
        }

        var report = Read("report.md");
        Assert.Contains("# Pickle bug report", report, StringComparison.Ordinal);
        Assert.Contains("## pk doctor", report, StringComparison.Ordinal);
        Assert.Contains("PowerShell", report, StringComparison.Ordinal);
        Assert.DoesNotContain("sekrit", Read("config.json"), StringComparison.Ordinal);
        Assert.DoesNotContain("abcdef123456", Read("logs/pickle-20260101.log"), StringComparison.Ordinal);
        Assert.DoesNotContain("private-history", string.Concat(names.Select(Read)), StringComparison.Ordinal);

        Assert.ThrowsAny<Exception>(() => t.Run("pk bugreport --bogus"));
        var noLogs = Path.Combine(Path.GetDirectoryName(zip)!, "nologs.zip");
        t.Run($"pk bugreport --out '{noLogs}' --no-logs");
        using var second = ZipFile.OpenRead(noLogs);
        Assert.DoesNotContain(second.Entries, e => e.FullName.StartsWith("logs/", StringComparison.Ordinal));
    }
}
