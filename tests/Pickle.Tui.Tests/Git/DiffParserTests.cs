using Pickle.Tui.Panels.Git;

namespace Pickle.Tui.Tests.Git;

public class DiffParserTests
{
    private const string TwoHunks =
        "diff --git a/src/app.txt b/src/app.txt\n" +
        "index 1111111..2222222 100644\n" +
        "--- a/src/app.txt\n" +
        "+++ b/src/app.txt\n" +
        "@@ -1,4 +1,4 @@ header\n" +
        " one\n" +
        "-two\n" +
        "+TWO\n" +
        " three\n" +
        " four\n" +
        "@@ -10,3 +10,5 @@ void Main()\n" +
        " ten\n" +
        "+new a\n" +
        "+new b\n" +
        " eleven\r\n" +
        " twelve\n";

    [Fact]
    public void ParsesFilesHunksAndLines()
    {
        var file = Assert.Single(DiffParser.Parse(TwoHunks));

        Assert.Equal("src/app.txt", file.Path);
        Assert.Equal("src/app.txt", file.OldPath);
        Assert.False(file.IsBinary);
        Assert.True(file.CanStagePartially);
        Assert.Equal(4, file.HeaderLines.Count);
        Assert.Equal(2, file.Hunks.Count);

        var first = file.Hunks[0];
        Assert.Equal((1, 4, 1, 4), (first.OldStart, first.OldCount, first.NewStart, first.NewCount));
        Assert.Equal("header", first.Section);
        Assert.Equal(
            [DiffLineKind.Context, DiffLineKind.Removed, DiffLineKind.Added, DiffLineKind.Context, DiffLineKind.Context],
            first.Lines.Select(l => l.Kind));
        Assert.Equal("TWO", first.Lines[2].Content);

        var second = file.Hunks[1];
        Assert.Equal((10, 3, 10, 5), (second.OldStart, second.OldCount, second.NewStart, second.NewCount));
        Assert.Equal("void Main()", second.Section);
        Assert.Equal(" eleven\r", second.Lines[3].Text);
    }

    [Fact]
    public void HunkPatchKeepsHeadersAndOnlyThatHunk()
    {
        var file = DiffParser.Parse(TwoHunks)[0];
        var patch = DiffParser.BuildHunkPatch(file, file.Hunks[1]);
        Assert.Equal(
            "diff --git a/src/app.txt b/src/app.txt\nindex 1111111..2222222 100644\n--- a/src/app.txt\n+++ b/src/app.txt\n" +
            "@@ -10,3 +10,5 @@ void Main()\n ten\n+new a\n+new b\n eleven\r\n twelve\n",
            patch);
    }

    [Fact]
    public void ForwardLinePatchDropsUnselectedAddsAndKeepsUnselectedRemovesAsContext()
    {
        var file = DiffParser.Parse(TwoHunks)[0];

        var addOnly = DiffParser.BuildLinePatch(file, file.Hunks[0], new HashSet<int> { 2 }, reverse: false);
        Assert.EndsWith("@@ -1,4 +1,5 @@ header\n one\n two\n+TWO\n three\n four\n", addOnly, StringComparison.Ordinal);

        var removeOnly = DiffParser.BuildLinePatch(file, file.Hunks[0], new HashSet<int> { 1 }, reverse: false);
        Assert.EndsWith("@@ -1,4 +1,3 @@ header\n one\n-two\n three\n four\n", removeOnly, StringComparison.Ordinal);

        var oneOfTwo = DiffParser.BuildLinePatch(file, file.Hunks[1], new HashSet<int> { 2 }, reverse: false);
        Assert.EndsWith("@@ -10,3 +10,4 @@ void Main()\n ten\n+new b\n eleven\r\n twelve\n", oneOfTwo, StringComparison.Ordinal);

        Assert.Null(DiffParser.BuildLinePatch(file, file.Hunks[0], new HashSet<int> { 0, 3 }, reverse: false));
        Assert.Null(DiffParser.BuildLinePatch(file, file.Hunks[0], new HashSet<int>(), reverse: false));
    }

    [Fact]
    public void ReverseLinePatchSwapsTheRoles()
    {
        var file = DiffParser.Parse(TwoHunks)[0];

        var unstageRemoval = DiffParser.BuildLinePatch(file, file.Hunks[0], new HashSet<int> { 1 }, reverse: true);
        Assert.EndsWith("@@ -1,4 +1,4 @@ header\n one\n-two\n TWO\n three\n four\n", unstageRemoval, StringComparison.Ordinal);

        var unstageAdd = DiffParser.BuildLinePatch(file, file.Hunks[0], new HashSet<int> { 2 }, reverse: true);
        Assert.EndsWith("@@ -1,3 +1,4 @@ header\n one\n+TWO\n three\n four\n", unstageAdd, StringComparison.Ordinal);
    }

    [Fact]
    public void NoNewlineMarkerFollowsItsLine()
    {
        const string diff =
            "diff --git a/f b/f\n--- a/f\n+++ b/f\n@@ -1,2 +1,2 @@\n a\n-b\n\\ No newline at end of file\n+c\n\\ No newline at end of file\n";
        var file = DiffParser.Parse(diff)[0];
        Assert.Equal(DiffLineKind.NoNewline, file.Hunks[0].Lines[2].Kind);

        var onlyAdd = DiffParser.BuildLinePatch(file, file.Hunks[0], new HashSet<int> { 3 }, reverse: true);
        Assert.EndsWith("@@ -1 +1,2 @@\n a\n+c\n\\ No newline at end of file\n", onlyAdd, StringComparison.Ordinal);

        var onlyRemove = DiffParser.BuildLinePatch(file, file.Hunks[0], new HashSet<int> { 1 }, reverse: false);
        Assert.EndsWith("@@ -1,2 +1 @@\n a\n-b\n\\ No newline at end of file\n", onlyRemove, StringComparison.Ordinal);
    }

    [Fact]
    public void PartialNewFileReverseBecomesAModification()
    {
        const string diff =
            "diff --git a/n.txt b/n.txt\nnew file mode 100644\nindex 0000000..3333333\n--- /dev/null\n+++ b/n.txt\n@@ -0,0 +1,3 @@\n+x\n+y\n+z\n";
        var file = DiffParser.Parse(diff)[0];
        Assert.True(file.IsNewFile);
        Assert.Null(file.OldPath);
        Assert.Equal("n.txt", file.Path);

        var forward = DiffParser.BuildLinePatch(file, file.Hunks[0], new HashSet<int> { 0, 2 }, reverse: false);
        Assert.Equal("diff --git a/n.txt b/n.txt\nnew file mode 100644\nindex 0000000..3333333\n--- /dev/null\n+++ b/n.txt\n@@ -0,0 +1,2 @@\n+x\n+z\n", forward);

        var reverse = DiffParser.BuildLinePatch(file, file.Hunks[0], new HashSet<int> { 1 }, reverse: true);
        Assert.Equal("diff --git a/n.txt b/n.txt\nindex 0000000..3333333\n--- a/n.txt\n+++ b/n.txt\n@@ -1,2 +1,3 @@\n x\n+y\n z\n", reverse);
    }

    [Fact]
    public void PartialDeletionBecomesAModification()
    {
        const string diff =
            "diff --git a/d.txt b/d.txt\ndeleted file mode 100644\nindex 3333333..0000000\n--- a/d.txt\n+++ /dev/null\n@@ -1,2 +0,0 @@\n-x\n-y\n";
        var file = DiffParser.Parse(diff)[0];
        Assert.True(file.IsDeletedFile);
        Assert.Equal("d.txt", file.Path);
        Assert.Null(file.NewPath);

        var patch = DiffParser.BuildLinePatch(file, file.Hunks[0], new HashSet<int> { 0 }, reverse: false);
        Assert.Equal("diff --git a/d.txt b/d.txt\nindex 3333333..0000000\n--- a/d.txt\n+++ b/d.txt\n@@ -1,2 +1 @@\n-x\n y\n", patch);
    }

    [Fact]
    public void ParsesMultipleFilesBinaryRenamesAndQuotedPaths()
    {
        const string diff =
            "diff --git a/img.png b/img.png\nindex 1..2 100644\nBinary files a/img.png and b/img.png differ\n" +
            "diff --git a/old name.txt b/new name.txt\nsimilarity index 100%\nrename from old name.txt\nrename to new name.txt\n" +
            "diff --git \"a/t\\tab.txt\" \"b/t\\tab.txt\"\n--- \"a/t\\tab.txt\"\n+++ \"b/t\\tab.txt\"\n@@ -1 +1 @@\n-a\n+b\n" +
            "diff --git a/café x.txt b/café x.txt\nnew file mode 100644\n" +
            "diff --git \"a/\\303\\274.txt\" \"b/\\303\\274.txt\"\n--- \"a/\\303\\274.txt\"\n+++ \"b/\\303\\274.txt\"\n@@ -1 +1 @@\n-a\n+b\n";

        var files = DiffParser.Parse(diff);

        Assert.Equal(5, files.Count);
        Assert.True(files[0].IsBinary);
        Assert.False(files[0].CanStagePartially);
        Assert.Equal("img.png", files[0].Path);
        Assert.Equal(("old name.txt", "new name.txt"), (files[1].OldPath, files[1].NewPath));
        Assert.Equal("t\tab.txt", files[2].Path);
        Assert.Equal("café x.txt", files[3].Path);
        Assert.Empty(files[3].Hunks);
        Assert.Equal("ü.txt", files[4].Path);
    }

    [Fact]
    public void CombinedConflictDiffsAreDisplayOnly()
    {
        const string diff =
            "diff --cc c.txt\nindex 1,2..0000000\n--- a/c.txt\n+++ b/c.txt\n@@@ -1,1 -1,1 +1,5 @@@\n++<<<<<<< HEAD\n +main\n++=======\n+ other\n++>>>>>>> other\n";
        var file = Assert.Single(DiffParser.Parse(diff));
        Assert.True(file.IsCombined);
        Assert.False(file.CanStagePartially);
        Assert.Equal("c.txt", file.Path);
        Assert.All(file.Hunks[0].Lines, l => Assert.Equal(DiffLineKind.Added, l.Kind));
    }

    [Fact]
    public void EmptyOrGarbageInputHasNoFiles()
    {
        Assert.Empty(DiffParser.Parse(null));
        Assert.Empty(DiffParser.Parse(string.Empty));
        Assert.Empty(DiffParser.Parse("warning: something\n"));
    }
}
