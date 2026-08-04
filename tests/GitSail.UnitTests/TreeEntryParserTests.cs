using GitSail.Domain;
using GitSail.Git.Parsing;
using GitSail.Testing;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies bounded exact parsing of long-format NUL-delimited Git tree entries.
/// </summary>
[TestClass]
public sealed class TreeEntryParserTests
{
    /// <summary>
    /// Verifies every supported tree mode retains its exact kind, object, size, and name.
    /// </summary>
    [TestMethod]
    public void Parse_WithEverySupportedMode_ReturnsExactEntries()
    {
        var output = new List<byte>();
        AddEntry(output, "040000 tree 1111111111111111111111111111111111111111       -"u8, "directory"u8);
        AddEntry(output, "100644 blob 2222222222222222222222222222222222222222      12"u8, "file name.txt"u8);
        AddEntry(output, "100755 blob 3333333333333333333333333333333333333333      31"u8, "tool"u8);
        AddEntry(output, "120000 blob 4444444444444444444444444444444444444444       6"u8, "link"u8);
        AddEntry(output, "160000 commit 5555555555555555555555555555555555555555       -"u8, "module"u8);

        var entries = new TreeEntryParser().Parse(output.ToArray());

        Assert.HasCount(5, entries);
        TestSeq.AreEqual(
            new[]
            {
                TreeEntryKind.Tree,
                TreeEntryKind.RegularFile,
                TreeEntryKind.ExecutableFile,
                TreeEntryKind.SymbolicLink,
                TreeEntryKind.GitLink,
            },
            entries.Select(static entry => entry.Kind));
        Assert.IsNull(entries[0].Size);
        Assert.AreEqual(12L, entries[1].Size);
        Assert.AreEqual("file name.txt", entries[1].Name.DisplayText);
        Assert.AreEqual("5555555555555555555555555555555555555555", entries[4].ObjectId.ToString());
    }

    /// <summary>
    /// Verifies Unix tree names retain exact invalid UTF-8 bytes without display round-tripping.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public void Parse_WithNonUtf8UnixName_RetainsExactBytes()
    {
        var output = new List<byte>();
        AddEntry(
            output,
            "100644 blob 2222222222222222222222222222222222222222       2"u8,
            [(byte)'a', 0xff]);

        var entry = TestSeq.Single(new TreeEntryParser().Parse(output.ToArray()));

        TestSeq.AreEqual(new byte[] { (byte)'a', 0xff }, entry.Name.GetUnixBytes().ToArray());
        Assert.AreEqual("a<0xFF>", entry.Name.DisplayText);
    }

    /// <summary>
    /// Verifies inconsistent mode and object type metadata fails closed.
    /// </summary>
    [TestMethod]
    public void Parse_WithInconsistentModeAndType_ThrowsInvalidDataException()
    {
        var output = new List<byte>();
        AddEntry(output, "040000 blob 1111111111111111111111111111111111111111       -"u8, "directory"u8);

        Assert.ThrowsExactly<InvalidDataException>(() =>
            new TreeEntryParser().Parse(output.ToArray()));
    }

    /// <summary>
    /// Verifies tree output without a final NUL terminator is rejected.
    /// </summary>
    [TestMethod]
    public void Parse_WithoutRecordTerminator_ThrowsInvalidDataException()
    {
        var output = new List<byte>();
        output.AddRange("100644 blob 2222222222222222222222222222222222222222       2\tfile"u8.ToArray());

        Assert.ThrowsExactly<InvalidDataException>(() =>
            new TreeEntryParser().Parse(output.ToArray()));
    }

    private static void AddEntry(
        List<byte> output,
        ReadOnlySpan<byte> metadata,
        ReadOnlySpan<byte> name)
    {
        output.AddRange(metadata.ToArray());
        output.Add((byte)'\t');
        output.AddRange(name.ToArray());
        output.Add(0);
    }
}
