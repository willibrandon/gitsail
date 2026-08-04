using GitSail.Domain;
using GitSail.Git.Parsing;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies bounded exact parsing of structured NUL-framed Git history records.
/// </summary>
[TestClass]
public sealed class HistoryLogParserTests
{
    /// <summary>
    /// Verifies a merge commit retains its exact parents, author, refs, signature, and message.
    /// </summary>
    [TestMethod]
    public void Parse_WithCompleteMergeRecord_ReturnsStructuredCommit()
    {
        var output = new List<byte>();
        AddRecord(
            output,
            "1111111111111111111111111111111111111111"u8,
            "2222222222222222222222222222222222222222 3333333333333333333333333333333333333333"u8,
            "Test Author"u8,
            "author@example.invalid"u8,
            "2026-08-04T09:47:23-07:00"u8,
            "HEAD -> refs/heads/main, refs/tags/v1"u8,
            "G"u8,
            "Merge topic"u8,
            "Body line one\nBody line two\n"u8);

        var commits = new HistoryLogParser().Parse(output.ToArray());

        Assert.HasCount(1, commits);
        Assert.AreEqual("1111111111111111111111111111111111111111", commits[0].ObjectId.ToString());
        Assert.HasCount(2, commits[0].Parents);
        Assert.AreEqual("2222222222222222222222222222222222222222", commits[0].Parents[0].ToString());
        Assert.AreEqual("Test Author", System.Text.Encoding.UTF8.GetString(commits[0].AuthorName.Span));
        Assert.AreEqual(CommitSignatureStatus.Good, commits[0].SignatureStatus);
        Assert.AreEqual("Merge topic", System.Text.Encoding.UTF8.GetString(commits[0].Subject.Span));
        Assert.AreEqual(-7, commits[0].AuthoredAt.Offset.Hours);
    }

    /// <summary>
    /// Verifies an empty stream represents an empty repository history.
    /// </summary>
    [TestMethod]
    public void Parse_WithEmptyOutput_ReturnsEmptyCatalog()
    {
        var commits = new HistoryLogParser().Parse([]);

        Assert.IsEmpty(commits);
    }

    /// <summary>
    /// Verifies a missing record boundary fails without returning a partial commit.
    /// </summary>
    [TestMethod]
    public void Parse_WithoutRecordBoundary_ThrowsInvalidDataException()
    {
        var output = new List<byte>();
        AddField(output, "1111111111111111111111111111111111111111"u8);
        AddField(output, []);
        AddField(output, "Author"u8);
        AddField(output, "author@example.invalid"u8);
        AddField(output, "2026-08-04T09:47:23-07:00"u8);
        AddField(output, []);
        AddField(output, "N"u8);
        AddField(output, "subject"u8);
        AddField(output, []);

        Assert.ThrowsExactly<InvalidDataException>(() =>
            new HistoryLogParser().Parse(output.ToArray()));
    }

    /// <summary>
    /// Verifies malformed parent spacing cannot create an ambiguous graph identity.
    /// </summary>
    [TestMethod]
    public void Parse_WithMalformedParentList_ThrowsInvalidDataException()
    {
        var output = new List<byte>();
        AddRecord(
            output,
            "1111111111111111111111111111111111111111"u8,
            "2222222222222222222222222222222222222222  3333333333333333333333333333333333333333"u8,
            "Author"u8,
            "author@example.invalid"u8,
            "2026-08-04T09:47:23-07:00"u8,
            [],
            "N"u8,
            "subject"u8,
            []);

        Assert.ThrowsExactly<InvalidDataException>(() =>
            new HistoryLogParser().Parse(output.ToArray()));
    }

    /// <summary>
    /// Verifies an unknown signature status fails instead of being presented as trusted.
    /// </summary>
    [TestMethod]
    public void Parse_WithUnknownSignatureStatus_ThrowsInvalidDataException()
    {
        var output = new List<byte>();
        AddRecord(
            output,
            "1111111111111111111111111111111111111111"u8,
            [],
            "Author"u8,
            "author@example.invalid"u8,
            "2026-08-04T09:47:23-07:00"u8,
            [],
            "?"u8,
            "subject"u8,
            []);

        Assert.ThrowsExactly<InvalidDataException>(() =>
            new HistoryLogParser().Parse(output.ToArray()));
    }

    private static void AddRecord(
        List<byte> output,
        ReadOnlySpan<byte> objectId,
        ReadOnlySpan<byte> parents,
        ReadOnlySpan<byte> authorName,
        ReadOnlySpan<byte> authorEmail,
        ReadOnlySpan<byte> authoredAt,
        ReadOnlySpan<byte> decorations,
        ReadOnlySpan<byte> signature,
        ReadOnlySpan<byte> subject,
        ReadOnlySpan<byte> body)
    {
        AddField(output, objectId);
        AddField(output, parents);
        AddField(output, authorName);
        AddField(output, authorEmail);
        AddField(output, authoredAt);
        AddField(output, decorations);
        AddField(output, signature);
        AddField(output, subject);
        AddField(output, body);
        output.Add(0);
    }

    private static void AddField(List<byte> output, ReadOnlySpan<byte> value)
    {
        output.AddRange(value.ToArray());
        output.Add(0);
    }
}
