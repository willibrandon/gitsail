using GitSail.Domain;
using GitSail.Git.Parsing;
using System.Text;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies strict unified-hunk indexing and exact-byte complete-hunk selection.
/// </summary>
[TestClass]
public sealed class RawPatchParserTests
{
    /// <summary>
    /// Verifies header, hunk, line, range, and presentation-line offsets for multiple hunks.
    /// </summary>
    [TestMethod]
    public void Parse_WithMultipleHunks_ReturnsValidatedExactSlices()
    {
        var header = "diff --git a/file.txt b/file.txt\n" +
            "index 1111111..2222222 100644\n" +
            "--- a/file.txt\n" +
            "+++ b/file.txt\n";
        var first = "@@ -1,2 +1,2 @@\n context\n-old\n+new\n";
        var second = "@@ -10 +10,2 @@ label\n-old2\n+new2\n+more\n\\ No newline at end of file\n";
        var bytes = Encoding.UTF8.GetBytes(header + first + second);

        var index = RawPatchParser.Parse(bytes);

        Assert.AreEqual(Encoding.UTF8.GetByteCount(header), index.HeaderLength);
        Assert.HasCount(2, index.Hunks);
        var firstHunk = index.Hunks[0];
        Assert.AreEqual(1, firstHunk.OldStart);
        Assert.AreEqual(2, firstHunk.OldCount);
        Assert.AreEqual(1, firstHunk.NewStart);
        Assert.AreEqual(2, firstHunk.NewCount);
        Assert.AreEqual(5, firstHunk.StartLineNumber);
        Assert.AreEqual(8, firstHunk.EndLineNumber);
        Assert.HasCount(3, firstHunk.Lines);
        Assert.AreEqual(RawPatchLineKind.Context, firstHunk.Lines[0].Kind);
        Assert.AreEqual(RawPatchLineKind.Deletion, firstHunk.Lines[1].Kind);
        Assert.AreEqual(RawPatchLineKind.Addition, firstHunk.Lines[2].Kind);
        var secondHunk = index.Hunks[1];
        Assert.AreEqual(10, secondHunk.OldStart);
        Assert.AreEqual(1, secondHunk.OldCount);
        Assert.AreEqual(10, secondHunk.NewStart);
        Assert.AreEqual(2, secondHunk.NewCount);
        Assert.AreSame(secondHunk, index.FindHunkAtLine(11));
        Assert.IsNull(index.FindHunkAtLine(4));
        Assert.AreEqual(RawPatchLineKind.NoNewlineMarker, secondHunk.Lines[^1].Kind);
        Assert.AreSame(firstHunk, index.FindNextHunk(1));
        Assert.AreSame(secondHunk, index.FindNextHunk(firstHunk.StartLineNumber));
        Assert.AreSame(firstHunk, index.FindPreviousHunk(firstHunk.StartLineNumber + 1));
        Assert.AreSame(firstHunk, index.FindPreviousHunk(secondHunk.StartLineNumber));
    }

    /// <summary>
    /// Verifies complete-hunk selection copies the unchanged header and exact selected bytes only.
    /// </summary>
    [TestMethod]
    public void BuildSingleHunk_WithSecondHunk_PreservesExactOriginalBytes()
    {
        const string header = "diff --git a/file.txt b/file.txt\n--- a/file.txt\n+++ b/file.txt\n";
        const string first = "@@ -1 +1 @@\n-old\n+new\n";
        const string second = "@@ -9 +9 @@\n-old-two\n+new-two\n";
        var bytes = Encoding.UTF8.GetBytes(header + first + second);
        var index = RawPatchParser.Parse(bytes);

        var selected = RawPatchSelectionBuilder.BuildSingleHunk(bytes, index, index.Hunks[1]);

        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(header + second), selected);
    }

    /// <summary>
    /// Verifies forward line selection retains old-side unselected content and exact byte terminators.
    /// </summary>
    [TestMethod]
    public void BuildSelectedLines_WithOldSideSelection_StagesOnlyRequestedReplacement()
    {
        const string header = "diff --git a/file.txt b/file.txt\r\n--- a/file.txt\r\n+++ b/file.txt\r\n";
        const string hunk = "@@ -1,5 +1,5 @@ method\r\n same one\r\n-old one\r\n+new one\r\n" +
            " same two\r\n-old two\r\n+new two\r\n same three\r\n";
        var bytes = Encoding.UTF8.GetBytes(header + hunk);
        var index = RawPatchParser.Parse(bytes);
        var selectedLines = new HashSet<int>
        {
            index.Hunks[0].Lines.Single(line =>
                line.Kind == RawPatchLineKind.Deletion && line.LineNumber == 6).LineNumber,
            index.Hunks[0].Lines.Single(line =>
                line.Kind == RawPatchLineKind.Addition && line.LineNumber == 7).LineNumber,
        };

        var selected = RawPatchSelectionBuilder.BuildSelectedLines(
            bytes,
            index,
            selectedLines,
            RawPatchSelectionSide.PreserveOldSide);

        var expected = header + "@@ -1,5 +1,5 @@ method\r\n same one\r\n-old one\r\n+new one\r\n" +
            " same two\r\n old two\r\n same three\r\n";
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(expected), selected);
    }

    /// <summary>
    /// Verifies reverse line selection retains new-side content and its attached no-newline marker.
    /// </summary>
    [TestMethod]
    public void BuildSelectedLines_WithNewSideSelection_UnstagesOnlyRequestedReplacement()
    {
        const string header = "diff --git a/file.txt b/file.txt\n--- a/file.txt\n+++ b/file.txt\n";
        const string hunk = "@@ -1,2 +1,2 @@\n-old one\n+new one\n-old two\n+new two\n" +
            "\\ No newline at end of file\n";
        var bytes = Encoding.UTF8.GetBytes(header + hunk);
        var index = RawPatchParser.Parse(bytes);
        var selectedLines = index.Hunks[0].Lines
            .Where(line => line.LineNumber is 7 or 8)
            .Select(static line => line.LineNumber)
            .ToHashSet();

        var selected = RawPatchSelectionBuilder.BuildSelectedLines(
            bytes,
            index,
            selectedLines,
            RawPatchSelectionSide.PreserveNewSide);

        var expected = header + "@@ -1,2 +1,2 @@\n new one\n-old two\n+new two\n" +
            "\\ No newline at end of file\n";
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(expected), selected);
    }

    /// <summary>
    /// Verifies discontiguous selected changes retain multiple original hunks without intervening bytes.
    /// </summary>
    [TestMethod]
    public void BuildSelectedLines_WithDiscontiguousHunks_EmitsEachSelectedHunk()
    {
        const string header = "diff --git a/file.txt b/file.txt\n--- a/file.txt\n+++ b/file.txt\n";
        const string first = "@@ -1 +1 @@ first\n-old one\n+new one\n";
        const string second = "@@ -9 +9 @@ second\n-old two\n+new two\n";
        var bytes = Encoding.UTF8.GetBytes(header + first + second);
        var index = RawPatchParser.Parse(bytes);
        var selectedLines = index.Hunks
            .SelectMany(static hunk => hunk.Lines)
            .Where(static line => line.Kind == RawPatchLineKind.Addition)
            .Select(static line => line.LineNumber)
            .ToHashSet();

        var selected = RawPatchSelectionBuilder.BuildSelectedLines(
            bytes,
            index,
            selectedLines,
            RawPatchSelectionSide.PreserveOldSide);

        var expected = header +
            "@@ -1 +1,2 @@ first\n old one\n+new one\n" +
            "@@ -9 +9,2 @@ second\n old two\n+new two\n";
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(expected), selected);
    }

    /// <summary>
    /// Verifies mismatched hunk counts fail closed before any patch can be selected.
    /// </summary>
    [TestMethod]
    public void Parse_WithMismatchedCounts_ThrowsInvalidDataException()
    {
        var bytes = "diff --git a/file b/file\n--- a/file\n+++ b/file\n@@ -1,2 +1 @@\n-old\n+new\n"u8.ToArray();

        Assert.ThrowsExactly<InvalidDataException>(() => RawPatchParser.Parse(bytes));
    }

    /// <summary>
    /// Verifies only Git's exact no-final-newline control line is accepted inside a hunk.
    /// </summary>
    [TestMethod]
    public void Parse_WithMalformedNoNewlineMarker_ThrowsInvalidDataException()
    {
        var bytes = "diff --git a/file b/file\n--- a/file\n+++ b/file\n@@ -1 +1 @@\n-old\n+new\n\\ unsafe marker\n"u8.ToArray();

        Assert.ThrowsExactly<InvalidDataException>(() => RawPatchParser.Parse(bytes));
    }

    /// <summary>
    /// Verifies bytes adjoining the closing hunk marker fail strict structural validation.
    /// </summary>
    [TestMethod]
    public void Parse_WithMalformedClosingMarker_ThrowsInvalidDataException()
    {
        var bytes = "diff --git a/file b/file\n--- a/file\n+++ b/file\n@@ -1 +1 @@invalid\n-old\n+new\n"u8.ToArray();

        Assert.ThrowsExactly<InvalidDataException>(() => RawPatchParser.Parse(bytes));
    }
}
