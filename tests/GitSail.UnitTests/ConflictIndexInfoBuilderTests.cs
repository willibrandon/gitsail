using GitSail.Domain;
using GitSail.Git.Execution;
using GitSail.Testing;
using System.Text;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies exact NUL-delimited index-info transactions for conflict resolution and rollback.
/// </summary>
[TestClass]
public sealed class ConflictIndexInfoBuilderTests
{
    /// <summary>
    /// Verifies resolution removes unmerged stages before adding one exact stage-zero blob.
    /// </summary>
    [TestMethod]
    public void BuildResolved_WithExactPath_ReturnsRemovalAndStageZeroRecords()
    {
        var path = CreatePath("dir/file name.txt");
        var objectId = CreateObjectId("1111111111111111111111111111111111111111");

        var actual = ConflictIndexInfoBuilder.BuildResolved(
            path,
            GitFileMode.RegularFile,
            objectId);

        var expected = Encoding.UTF8.GetBytes(
            "0 0000000000000000000000000000000000000000\tdir/file name.txt\0" +
            "100644 1111111111111111111111111111111111111111 0\tdir/file name.txt\0");
        TestSeq.AreEqual(expected, actual);
    }

    /// <summary>
    /// Verifies rollback removes stage zero and restores only the originally present higher stages.
    /// </summary>
    [TestMethod]
    public void BuildUnmerged_WithMissingBase_ReturnsExactOursAndTheirsStages()
    {
        var path = CreatePath("added.txt");
        var ours = new ConflictStage(
            GitFileMode.ExecutableFile,
            CreateObjectId("2222222222222222222222222222222222222222"));
        var theirs = new ConflictStage(
            GitFileMode.RegularFile,
            CreateObjectId("3333333333333333333333333333333333333333"));

        var actual = ConflictIndexInfoBuilder.BuildUnmerged(
            path,
            new ConflictStages(Base: null, ours, theirs, GitFileMode.RegularFile),
            RepositoryObjectFormat.Sha1);

        var expected = Encoding.UTF8.GetBytes(
            "0 0000000000000000000000000000000000000000\tadded.txt\0" +
            "100755 2222222222222222222222222222222222222222 2\tadded.txt\0" +
            "100644 3333333333333333333333333333333333333333 3\tadded.txt\0");
        TestSeq.AreEqual(expected, actual);
    }

    private static ObjectId CreateObjectId(string value)
    {
        Assert.IsTrue(ObjectId.TryParseHex(Encoding.ASCII.GetBytes(value), out var objectId));
        Assert.IsNotNull(objectId);
        return objectId;
    }

    private static GitPath CreatePath(string path)
        => OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath(path)
            : GitPath.FromUnixBytes(Encoding.UTF8.GetBytes(path));
}
