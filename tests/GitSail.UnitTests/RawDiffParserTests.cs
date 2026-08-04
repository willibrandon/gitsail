using GitSail.Domain;
using GitSail.Git.Execution;
using GitSail.Git.Parsing;
using System.Text;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies bounded file indexing over exact raw patch bytes and stream boundaries.
/// </summary>
[TestClass]
public sealed class RawDiffParserTests
{
    /// <summary>
    /// Verifies exact offsets when one metadata line spans multiple parser read buffers.
    /// </summary>
    [TestMethod]
    public async Task Parse_WithCrossBufferLine_RetainsExactFileBoundaries()
    {
        var firstHeader = "diff --git a/first.txt b/first.txt\n";
        var oversizedMetadata = new string('x', 70 * 1024) + "\n";
        var secondPatch = "diff --git a/second.txt b/second.txt\n@@ -1 +1 @@\n-old\n+new\n";
        var bytes = Encoding.UTF8.GetBytes(firstHeader + oversizedMetadata + secondPatch);
        using var spool = RawByteSpool.Create(128);
        await spool.AppendAsync(bytes, CancellationToken.None);

        var index = RawDiffParser.Parse(spool, new OperationGeneration(23));

        Assert.AreEqual(23L, index.Generation.Value);
        Assert.HasCount(2, index.Files);
        Assert.AreEqual(0L, index.Files[0].Offset);
        Assert.AreEqual(Encoding.UTF8.GetByteCount(firstHeader + oversizedMetadata), index.Files[0].Length);
        Assert.IsFalse(index.Files[0].HasHunks);
        Assert.AreEqual(index.Files[0].Length, index.Files[1].Offset);
        Assert.AreEqual(Encoding.UTF8.GetByteCount(secondPatch), index.Files[1].Length);
        Assert.IsTrue(index.Files[1].HasHunks);
        Assert.HasCount(1, index.Files[1].PatchIndex.Hunks);
        Assert.AreEqual(2, index.Files[1].PatchIndex.Hunks[0].StartLineNumber);
        var secondBytes = await spool.ReadSliceAsync(
            index.Files[1].Offset,
            checked((int)index.Files[1].Length),
            CancellationToken.None);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(secondPatch), secondBytes);
    }

    /// <summary>
    /// Verifies mutation slices remain complete when a hunk extends beyond the bounded presentation prefix.
    /// </summary>
    [TestMethod]
    public async Task ReadHunkPatchAsync_WithHunkBeyondPresentationPrefix_ReturnsCompleteExactBytes()
    {
        const int presentationLimit = 4 * 1024 * 1024;
        var fileHeader = "diff --git a/large.txt b/large.txt\n--- a/large.txt\n+++ b/large.txt\n"u8.ToArray();
        var hunkHeader = "@@ -1 +1 @@\n"u8.ToArray();
        var contentLength = presentationLimit + 128;
        var patch = new byte[fileHeader.Length + hunkHeader.Length + contentLength + 2];
        fileHeader.CopyTo(patch, 0);
        hunkHeader.CopyTo(patch, fileHeader.Length);
        var contentOffset = fileHeader.Length + hunkHeader.Length;
        patch[contentOffset] = (byte)' ';
        patch.AsSpan(contentOffset + 1, contentLength).Fill((byte)'x');
        patch[^1] = (byte)'\n';
        var spool = RawByteSpool.Create(128);
        await spool.AppendAsync(patch, CancellationToken.None);
        var index = RawDiffParser.Parse(spool, new OperationGeneration(4));
        using var document = new RawDiffDocument(spool, index);
        Assert.HasCount(1, index.Files);
        var file = index.Files[0];
        Assert.HasCount(1, file.PatchIndex.Hunks);
        var hunk = file.PatchIndex.Hunks[0];

        var prefix = await document.ReadFilePrefixAsync(file, presentationLimit, CancellationToken.None);
        var selectedPatch = await document.ReadHunkPatchAsync(file, hunk, CancellationToken.None);

        Assert.HasCount(presentationLimit, prefix);
        Assert.IsGreaterThan(presentationLimit, hunk.Length);
        CollectionAssert.AreEqual(patch, selectedPatch);
    }

    /// <summary>
    /// Verifies binary markers and a final line without a newline are indexed correctly.
    /// </summary>
    [TestMethod]
    public async Task Parse_WithBinaryPatchAndNoFinalNewline_ReportsCapabilities()
    {
        const string patch = "diff --git a/image.bin b/image.bin\nGIT binary patch\nliteral 1\nA";
        using var spool = RawByteSpool.Create(1024);
        await spool.AppendAsync(Encoding.UTF8.GetBytes(patch), CancellationToken.None);

        var index = RawDiffParser.Parse(spool, new OperationGeneration(1));

        Assert.HasCount(1, index.Files);
        Assert.IsTrue(index.Files[0].IsBinary);
        Assert.IsFalse(index.Files[0].HasHunks);
        Assert.AreEqual(Encoding.UTF8.GetByteCount(patch), index.Files[0].Length);
    }

    /// <summary>
    /// Verifies nonempty bytes before the first file header fail closed.
    /// </summary>
    [TestMethod]
    public async Task Parse_WithUnexpectedPreamble_ThrowsInvalidDataException()
    {
        using var spool = RawByteSpool.Create(1024);
        await spool.AppendAsync("unexpected\n"u8.ToArray(), CancellationToken.None);

        Assert.ThrowsExactly<InvalidDataException>(
            () => RawDiffParser.Parse(spool, new OperationGeneration(0)));
    }
}
