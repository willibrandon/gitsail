using GitSail.Domain;
using GitSail.Git.Parsing;
using GitSail.Testing;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies bounded byte parsing of Git porcelain version 2 status records.
/// </summary>
[TestClass]
public sealed class PorcelainV2StatusParserTests
{
    /// <summary>
    /// Verifies parsing of branch metadata, ordinary changes, renames, and an invalid UTF-8 path.
    /// </summary>
    [TestMethod]
    public void Parse_WithCompleteMixedResponse_ReturnsStructuredSnapshot()
    {
        var output = new List<byte>();
        AddRecord(output, "# branch.oid 0123456789abcdef0123456789abcdef01234567"u8);
        AddRecord(output, "# branch.head main"u8);
        AddRecord(output, "# branch.upstream origin/main"u8);
        AddRecord(output, "# branch.ab +2 -3"u8);
        AddRecord(output, "1 M. N... 100644 100644 100644 0123456789abcdef0123456789abcdef01234567 0123456789abcdef0123456789abcdef01234567 staged.txt"u8);
        AddRecord(output, "2 R. N... 100644 100644 100644 0123456789abcdef0123456789abcdef01234567 0123456789abcdef0123456789abcdef01234567 R100 renamed.txt"u8);
        AddRecord(output, "original.txt"u8);
        output.AddRange([0x3f, 0x20, 0x62, 0x61, 0x64, 0xff, 0x2e, 0x74, 0x78, 0x74, 0x00]);
        var parser = new PorcelainV2StatusParser();

        var snapshot = parser.Parse(output.ToArray(), CreateRepository(), new OperationGeneration(7));

        Assert.AreEqual(7L, snapshot.Generation.Value);
        Assert.AreEqual("0123456789abcdef0123456789abcdef01234567", snapshot.HeadObjectId?.ToString());
        Assert.AreEqual("main", snapshot.HeadName?.DisplayText);
        Assert.AreEqual("origin/main", snapshot.UpstreamName?.DisplayText);
        Assert.AreEqual(2, snapshot.AheadCount);
        Assert.AreEqual(3, snapshot.BehindCount);
        Assert.HasCount(3, snapshot.Entries);
        Assert.AreEqual(GitFileStatus.Modified, snapshot.Entries[0].IndexStatus);
        Assert.AreEqual(RepositoryStatusEntryKind.Rename, snapshot.Entries[1].Kind);
        Assert.AreEqual("renamed.txt", snapshot.Entries[1].Path.DisplayText);
        Assert.AreEqual("original.txt", snapshot.Entries[1].OriginalPath?.DisplayText);
        Assert.AreEqual(100, snapshot.Entries[1].SimilarityPercentage);
        if (OperatingSystem.IsWindows())
        {
            StringAssert.Contains(snapshot.Entries[2].Path.DisplayText, "bad", StringComparison.Ordinal);
        }
        else
        {
            Assert.AreEqual("bad<0xFF>.txt", snapshot.Entries[2].Path.DisplayText);
        }
    }

    /// <summary>
    /// Verifies an unmerged record retains exact base, ours, theirs, and worktree mode identities.
    /// </summary>
    [TestMethod]
    public void Parse_WithUnmergedRecord_ReturnsStructuredConflictStages()
    {
        var output = new List<byte>();
        AddRecord(
            output,
            "u UU N... 100644 100644 100755 100644 1111111111111111111111111111111111111111 2222222222222222222222222222222222222222 3333333333333333333333333333333333333333 conflict.txt"u8);
        var parser = new PorcelainV2StatusParser();

        var snapshot = parser.Parse(output.ToArray(), CreateRepository(), new OperationGeneration(1));

        var entry = TestSeq.Single(snapshot.Entries);
        Assert.AreEqual(RepositoryStatusEntryKind.Unmerged, entry.Kind);
        Assert.AreEqual(GitFileStatus.Unmerged, entry.IndexStatus);
        Assert.AreEqual(GitFileStatus.Unmerged, entry.WorkTreeStatus);
        Assert.IsNotNull(entry.ConflictStages);
        Assert.AreEqual(GitFileMode.RegularFile, entry.ConflictStages.Base?.Mode);
        Assert.AreEqual("1111111111111111111111111111111111111111", entry.ConflictStages.Base?.ObjectId.ToString());
        Assert.AreEqual(GitFileMode.RegularFile, entry.ConflictStages.Ours?.Mode);
        Assert.AreEqual("2222222222222222222222222222222222222222", entry.ConflictStages.Ours?.ObjectId.ToString());
        Assert.AreEqual(GitFileMode.ExecutableFile, entry.ConflictStages.Theirs?.Mode);
        Assert.AreEqual("3333333333333333333333333333333333333333", entry.ConflictStages.Theirs?.ObjectId.ToString());
        Assert.AreEqual(GitFileMode.RegularFile, entry.ConflictStages.WorkTreeMode);
    }

    /// <summary>
    /// Verifies an add/add conflict represents its absent merge-base stage explicitly as null.
    /// </summary>
    [TestMethod]
    public void Parse_WithMissingConflictBase_ReturnsNullBaseStage()
    {
        var output = new List<byte>();
        AddRecord(
            output,
            "u AA N... 000000 100644 100644 100644 0000000000000000000000000000000000000000 2222222222222222222222222222222222222222 3333333333333333333333333333333333333333 added.txt"u8);
        var parser = new PorcelainV2StatusParser();

        var snapshot = parser.Parse(output.ToArray(), CreateRepository(), new OperationGeneration(1));

        var stages = TestSeq.Single(snapshot.Entries).ConflictStages;
        Assert.IsNotNull(stages);
        Assert.IsNull(stages.Base);
        Assert.IsNotNull(stages.Ours);
        Assert.IsNotNull(stages.Theirs);
    }

    /// <summary>
    /// Verifies a present mode paired with a zero object identifier is rejected as inconsistent.
    /// </summary>
    [TestMethod]
    public void Parse_WithInconsistentConflictStage_ThrowsInvalidDataException()
    {
        var output = new List<byte>();
        AddRecord(
            output,
            "u UU N... 100644 100644 100644 100644 0000000000000000000000000000000000000000 2222222222222222222222222222222222222222 3333333333333333333333333333333333333333 conflict.txt"u8);
        var parser = new PorcelainV2StatusParser();

        Assert.ThrowsExactly<InvalidDataException>(() =>
            parser.Parse(output.ToArray(), CreateRepository(), new OperationGeneration(1)));
    }

    /// <summary>
    /// Verifies that a rename without its following original-path record fails closed.
    /// </summary>
    [TestMethod]
    public void Parse_WithTruncatedRename_ThrowsInvalidDataException()
    {
        var output = new List<byte>();
        AddRecord(output, "2 R. N... 100644 100644 100644 0123456789abcdef0123456789abcdef01234567 0123456789abcdef0123456789abcdef01234567 R100 renamed.txt"u8);
        var parser = new PorcelainV2StatusParser();

        Assert.ThrowsExactly<InvalidDataException>(() =>
            parser.Parse(output.ToArray(), CreateRepository(), new OperationGeneration(1)));
    }

    /// <summary>
    /// Verifies that an unterminated record fails closed.
    /// </summary>
    [TestMethod]
    public void Parse_WithMissingNulTerminator_ThrowsInvalidDataException()
    {
        var parser = new PorcelainV2StatusParser();

        Assert.ThrowsExactly<InvalidDataException>(() =>
            parser.Parse("? file.txt"u8, CreateRepository(), new OperationGeneration(1)));
    }

    private static void AddRecord(List<byte> destination, ReadOnlySpan<byte> record)
    {
        destination.AddRange(record.ToArray());
        destination.Add(0);
    }

    private static RepositoryLocation CreateRepository()
    {
        var root = OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath("C:\\repository")
            : GitPath.FromUnixBytes("/repository"u8);
        return new RepositoryLocation(
            root,
            root,
            root,
            Prefix: null,
            RepositoryObjectFormat.Sha1,
            IsBare: false);
    }
}
