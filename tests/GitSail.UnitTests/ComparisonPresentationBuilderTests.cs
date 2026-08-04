using GitSail.Domain;
using GitSail.Git.Parsing;
using GitSail.Ui;
using System.Text;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies unified and aligned two-pane presentation from immutable raw patch bytes.
/// </summary>
[TestClass]
public sealed class ComparisonPresentationBuilderTests
{
    /// <summary>
    /// Verifies replacement blocks align deletions and additions without losing context or hunk identity.
    /// </summary>
    [TestMethod]
    public void Build_WithReplacementBlock_ReturnsAlignedSideDocuments()
    {
        const string patch = "diff --git a/file.txt b/file.txt\n" +
            "--- a/file.txt\n" +
            "+++ b/file.txt\n" +
            "@@ -1,4 +1,3 @@\n" +
            " same\n" +
            "-old one\n" +
            "-old two\n" +
            "+new one\n" +
            " tail\n";
        var bytes = Encoding.UTF8.GetBytes(patch);
        var file = CreateFile(bytes);

        var presentation = ComparisonPresentationBuilder.Build(bytes, file, isTruncated: false);

        Assert.AreEqual(
            "@@ -1,4 +1,3 @@\n same\n-old one\n-old two\n tail\n",
            presentation.LeftText);
        Assert.AreEqual(
            "@@ -1,4 +1,3 @@\n same\n+new one\n\n tail\n",
            presentation.RightText);
        Assert.AreEqual(patch, presentation.UnifiedText);
        Assert.HasCount(1, presentation.UnifiedHunkLines);
        Assert.AreEqual(4, presentation.UnifiedHunkLines[0]);
        Assert.HasCount(1, presentation.SideHunkLines);
        Assert.AreEqual(1, presentation.SideHunkLines[0]);
        Assert.HasCount(2, presentation.UnifiedHighlights);
        AssertHighlight(presentation.UnifiedHighlights[0], 6, 2, 5, isAddition: false);
        AssertHighlight(presentation.UnifiedHighlights[1], 8, 2, 5, isAddition: true);
        Assert.HasCount(1, presentation.LeftHighlights);
        AssertHighlight(presentation.LeftHighlights[0], 3, 2, 5, isAddition: false);
        Assert.HasCount(1, presentation.RightHighlights);
        AssertHighlight(presentation.RightHighlights[0], 3, 2, 5, isAddition: true);
    }

    /// <summary>
    /// Verifies unsafe bytes remain visible tokens and bounded prefixes advertise truncation in every layout.
    /// </summary>
    [TestMethod]
    public void Build_WithInvalidByteAndTruncation_ReturnsSafeVisibleMarkers()
    {
        var header = "diff --git a/file.txt b/file.txt\n--- a/file.txt\n+++ b/file.txt\n@@ -1 +1 @@\n"u8.ToArray();
        var bytes = new byte[header.Length + 8];
        header.CopyTo(bytes, 0);
        "-old\n+"u8.CopyTo(bytes.AsSpan(header.Length));
        bytes[^2] = 0xff;
        bytes[^1] = (byte)'\n';
        var file = CreateFile(bytes);

        var presentation = ComparisonPresentationBuilder.Build(bytes, file, isTruncated: true);

        StringAssert.Contains(presentation.UnifiedText, "+<0xFF>");
        StringAssert.Contains(presentation.RightText, "+<0xFF>");
        StringAssert.Contains(presentation.LeftText, "presentation truncated");
        StringAssert.Contains(presentation.RightText, "presentation truncated");
    }

    /// <summary>
    /// Verifies intraline ranges retain complete Unicode text elements around changed words.
    /// </summary>
    [TestMethod]
    public void Build_WithUnicodeReplacement_ReturnsTextElementAlignedHighlights()
    {
        const string patch = "diff --git a/file.txt b/file.txt\n" +
            "--- a/file.txt\n" +
            "+++ b/file.txt\n" +
            "@@ -1 +1 @@\n" +
            "-prefix cafe\u0301 red suffix\n" +
            "+prefix cafe\u0301 blue suffix\n";
        var bytes = Encoding.UTF8.GetBytes(patch);
        var file = CreateFile(bytes);

        var presentation = ComparisonPresentationBuilder.Build(bytes, file, isTruncated: false);

        Assert.HasCount(2, presentation.UnifiedHighlights);
        AssertHighlight(presentation.UnifiedHighlights[0], 5, 15, 18, isAddition: false);
        AssertHighlight(presentation.UnifiedHighlights[1], 6, 15, 19, isAddition: true);
        AssertHighlight(presentation.LeftHighlights[0], 2, 15, 18, isAddition: false);
        AssertHighlight(presentation.RightHighlights[0], 2, 15, 19, isAddition: true);
    }

    private static RawDiffFile CreateFile(byte[] bytes)
    {
        var path = OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath("file.txt")
            : GitPath.FromUnixBytes("file.txt"u8);
        return new RawDiffFile(
            path,
            path,
            Offset: 0,
            bytes.Length,
            RawPatchParser.Parse(bytes),
            IsBinary: false);
    }

    private static void AssertHighlight(
        ComparisonHighlight highlight,
        int line,
        int startColumn,
        int endColumn,
        bool isAddition)
    {
        Assert.AreEqual(line, highlight.Line);
        Assert.AreEqual(startColumn, highlight.StartColumn);
        Assert.AreEqual(endColumn, highlight.EndColumn);
        Assert.AreEqual(isAddition, highlight.IsAddition);
    }
}
