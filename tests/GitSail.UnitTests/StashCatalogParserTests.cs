using GitSail.Git.Parsing;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies bounded exact parsing of explicit-format stash reflog records.
/// </summary>
[TestClass]
public sealed class StashCatalogParserTests
{
    /// <summary>
    /// Verifies ordered SHA-1 and SHA-256 entries retain exact subjects and timestamps.
    /// </summary>
    [TestMethod]
    public void Parse_WithCompleteMixedObjectFormats_ReturnsExactEntries()
    {
        var output = new List<byte>();
        AddRecord(
            output,
            "1111111111111111111111111111111111111111"u8,
            "refs/stash@{0}"u8,
            "On main: newest"u8,
            "1700000001"u8);
        AddRecord(
            output,
            "2222222222222222222222222222222222222222222222222222222222222222"u8,
            "refs/stash@{1}"u8,
            [(byte)'W', (byte)'I', (byte)'P', (byte)' ', 0x1b, 0xff],
            "1700000000"u8);

        var entries = new StashCatalogParser().Parse(output.ToArray());

        Assert.HasCount(2, entries);
        Assert.AreEqual("stash@{0}", entries[0].Selector);
        Assert.AreEqual("On main: newest", entries[0].DisplayMessage);
        Assert.AreEqual(1700000001, entries[0].CreatedAt.ToUnixTimeSeconds());
        Assert.AreEqual(
            "2222222222222222222222222222222222222222222222222222222222222222",
            entries[1].ObjectId.ToString());
        Assert.AreEqual("WIP <0x1B><0xFF>", entries[1].DisplayMessage);
    }

    /// <summary>
    /// Verifies an absent stash ref is represented by an empty byte stream and catalog.
    /// </summary>
    [TestMethod]
    public void Parse_WithEmptyOutput_ReturnsEmptyCatalog()
    {
        var entries = new StashCatalogParser().Parse([]);

        Assert.IsEmpty(entries);
    }

    /// <summary>
    /// Verifies a missing second NUL record boundary fails closed.
    /// </summary>
    [TestMethod]
    public void Parse_WithoutRecordBoundary_ThrowsInvalidDataException()
    {
        var output = new List<byte>();
        AddField(output, "1111111111111111111111111111111111111111"u8);
        AddField(output, "refs/stash@{0}"u8);
        AddField(output, "subject"u8);
        AddField(output, "1700000000"u8);

        Assert.ThrowsExactly<InvalidDataException>(() =>
            new StashCatalogParser().Parse(output.ToArray()));
    }

    /// <summary>
    /// Verifies a shortened or relative reflog selector cannot become an action target.
    /// </summary>
    [TestMethod]
    [DataRow("stash@{0}")]
    [DataRow("refs/stash@{01}")]
    [DataRow("refs/stash@{-1}")]
    [DataRow("refs/stash@{1}")]
    public void Parse_WithInvalidFirstSelector_ThrowsInvalidDataException(string selector)
    {
        var output = new List<byte>();
        AddRecord(
            output,
            "1111111111111111111111111111111111111111"u8,
            System.Text.Encoding.ASCII.GetBytes(selector),
            "subject"u8,
            "1700000000"u8);

        Assert.ThrowsExactly<InvalidDataException>(() =>
            new StashCatalogParser().Parse(output.ToArray()));
    }

    /// <summary>
    /// Verifies configured record limits are enforced before any partial result is returned.
    /// </summary>
    [TestMethod]
    public void Parse_WithOversizedRecord_ThrowsInvalidDataException()
    {
        var output = new List<byte>();
        AddRecord(
            output,
            "1111111111111111111111111111111111111111"u8,
            "refs/stash@{0}"u8,
            "long subject"u8,
            "1700000000"u8);

        Assert.ThrowsExactly<InvalidDataException>(() =>
            new StashCatalogParser(maximumRecordBytes: 16).Parse(output.ToArray()));
    }

    private static void AddRecord(
        List<byte> output,
        ReadOnlySpan<byte> objectId,
        ReadOnlySpan<byte> selector,
        ReadOnlySpan<byte> message,
        ReadOnlySpan<byte> timestamp)
    {
        AddField(output, objectId);
        AddField(output, selector);
        AddField(output, message);
        AddField(output, timestamp);
        output.Add(0);
    }

    private static void AddField(List<byte> output, ReadOnlySpan<byte> value)
    {
        output.AddRange(value.ToArray());
        output.Add(0);
    }
}
