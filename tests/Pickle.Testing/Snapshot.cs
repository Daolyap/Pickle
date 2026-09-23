using System.Runtime.CompilerServices;

namespace Pickle.Testing;

/// <summary>
/// Minimal text snapshot testing. Snapshots live in a <c>__snapshots__</c> folder next to the test file as
/// <c>{TestClass}.{Method}[.{name}].txt</c>.
/// <list type="bullet">
/// <item>Missing snapshot: written and the test passes locally; fails when <c>CI=true</c> (commit snapshots!).</item>
/// <item>Mismatch: fails with a diff and writes <c>*.received.txt</c> next to it (git-ignored).</item>
/// <item><c>PICKLE_UPDATE_SNAPSHOTS=1</c>: overwrite snapshots with the actual values.</item>
/// </list>
/// </summary>
public static class Snapshot
{
    public static void Match(
        string actual,
        string? name = null,
        [CallerFilePath] string testFile = "",
        [CallerMemberName] string testMethod = "")
    {
        actual = actual.Replace("\r\n", "\n", StringComparison.Ordinal);
        var dir = Path.Combine(Path.GetDirectoryName(testFile)!, "__snapshots__");
        var cls = Path.GetFileNameWithoutExtension(testFile);
        var file = Path.Combine(dir, $"{cls}.{testMethod}{(name is null ? string.Empty : "." + name)}.txt");
        var received = Path.ChangeExtension(file, ".received.txt");

        var update = Environment.GetEnvironmentVariable("PICKLE_UPDATE_SNAPSHOTS") is "1" or "true";
        if (!File.Exists(file) || update)
        {
            if (!update && Environment.GetEnvironmentVariable("CI") is "true" or "1")
            {
                throw new SnapshotMismatchException($"Snapshot missing in CI: {file}. Run tests locally and commit the snapshot.");
            }

            Directory.CreateDirectory(dir);
            File.WriteAllText(file, actual);
            File.Delete(received);
            return;
        }

        var expected = File.ReadAllText(file).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (expected == actual)
        {
            File.Delete(received);
            return;
        }

        File.WriteAllText(received, actual);
        throw new SnapshotMismatchException(
            $"Snapshot mismatch: {file}\n" +
            $"Actual written to {received}. Set PICKLE_UPDATE_SNAPSHOTS=1 to accept.\n" +
            Diff(expected, actual));
    }

    private static string Diff(string expected, string actual)
    {
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        var lines = new List<string>();
        for (var i = 0; i < Math.Max(e.Length, a.Length); i++)
        {
            var el = i < e.Length ? e[i] : "<missing>";
            var al = i < a.Length ? a[i] : "<missing>";
            if (el != al)
            {
                lines.Add($"line {i + 1}:\n  - {el}\n  + {al}");
            }

            if (lines.Count >= 10)
            {
                lines.Add("…");
                break;
            }
        }

        return string.Join('\n', lines);
    }
}

public sealed class SnapshotMismatchException(string message) : Exception(message);
