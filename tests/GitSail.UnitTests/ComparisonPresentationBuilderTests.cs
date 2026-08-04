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
}
