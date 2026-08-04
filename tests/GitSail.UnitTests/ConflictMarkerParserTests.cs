using GitSail.Domain;
using GitSail.Git.Parsing;
using System.Text;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies raw diff3 marker indexing and exact per-chunk resolution without text re-encoding.
/// </summary>
[TestClass]
public sealed class ConflictMarkerParserTests
{
    /// <summary>
    /// Verifies two CRLF marker blocks retain non-conflict bytes and apply independent choices.
    /// </summary>
    [TestMethod]
    public void Parse_WithTwoCompleteChunks_BuildsExactResolvedBytes()
    {
        var markers = new ConflictMarkerSet(markerSize: 7, token: "abc123");
        var content = Encoding.UTF8.GetBytes(
            "prefix\r\n" +
            "<<<<<<< gitsail-ours-abc123\r\n" +
            "ours one\r\n" +
            "||||||| gitsail-base-abc123\r\n" +
            "base one\r\n" +
            "=======\r\n" +
            "theirs one\r\n" +
            ">>>>>>> gitsail-theirs-abc123\r\n" +
            "middle\n" +
            "<<<<<<< gitsail-ours-abc123\n" +
            "ours two\n" +
            "||||||| gitsail-base-abc123\n" +
            "base two\n" +
            "=======\n" +
            "theirs two\n" +
            ">>>>>>> gitsail-theirs-abc123\n" +
            "suffix\n");

        var document = ConflictMarkerParser.Parse(content, markers);
        var resolved = document.BuildResolvedContent(
        [
            ConflictResolutionChoice.Base,
            ConflictResolutionChoice.Both,
        ]);

        Assert.HasCount(2, document.Chunks);
        Assert.AreEqual(
            "prefix\r\nbase one\r\nmiddle\nours two\ntheirs two\nsuffix\n",
            Encoding.UTF8.GetString(resolved));
    }

    /// <summary>
    /// Verifies an incomplete marker block fails closed instead of indexing ambiguous byte ranges.
    /// </summary>
    [TestMethod]
    public void Parse_WithMissingClosingMarker_ThrowsInvalidDataException()
    {
        var markers = new ConflictMarkerSet(markerSize: 7, token: "abc123");
        var content = Encoding.UTF8.GetBytes(
            "<<<<<<< gitsail-ours-abc123\n" +
            "ours\n" +
            "||||||| gitsail-base-abc123\n" +
            "base\n" +
            "=======\n" +
            "theirs\n");

        Assert.ThrowsExactly<InvalidDataException>(() => ConflictMarkerParser.Parse(content, markers));
    }

    /// <summary>
    /// Verifies resolution requires exactly one explicit choice for every indexed chunk.
    /// </summary>
    [TestMethod]
    public void BuildResolvedContent_WithMissingChoice_ThrowsArgumentException()
    {
        var markers = new ConflictMarkerSet(markerSize: 7, token: "abc123");
        var content = Encoding.UTF8.GetBytes(
            "<<<<<<< gitsail-ours-abc123\n" +
            "ours\n" +
            "||||||| gitsail-base-abc123\n" +
            "base\n" +
            "=======\n" +
            "theirs\n" +
            ">>>>>>> gitsail-theirs-abc123\n");
        var document = ConflictMarkerParser.Parse(content, markers);

        Assert.ThrowsExactly<ArgumentException>(() => document.BuildResolvedContent([]));
    }
}
