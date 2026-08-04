using GitSail.Domain;
using GitSail.Ui;
using System.Collections.Immutable;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies deterministic bounded lane construction from structured commit parents.
/// </summary>
[TestClass]
public sealed class HistoryGraphBuilderTests
{
    /// <summary>
    /// Verifies a merge produces separate active lanes that converge on the common parent.
    /// </summary>
    [TestMethod]
    public void Build_WithMergeHistory_ReturnsStableLanePrefixes()
    {
        var root = ParseObjectId("1111111111111111111111111111111111111111");
        var main = ParseObjectId("2222222222222222222222222222222222222222");
        var topic = ParseObjectId("3333333333333333333333333333333333333333");
        var merge = ParseObjectId("4444444444444444444444444444444444444444");
        var commits = ImmutableArray.Create(
            CreateCommit(merge, [main, topic]),
            CreateCommit(main, [root]),
            CreateCommit(topic, [root]),
            CreateCommit(root, []));

        var items = HistoryGraphBuilder.Build(commits);

        Assert.HasCount(4, items);
        Assert.AreEqual("●", items[0].Graph);
        StringAssert.Contains(items[1].Graph, "●", StringComparison.Ordinal);
        StringAssert.Contains(items[1].Graph, "│", StringComparison.Ordinal);
        StringAssert.Contains(items[2].Graph, "●", StringComparison.Ordinal);
        Assert.AreEqual(root, items[3].Commit.ObjectId);
    }

    private static HistoryCommit CreateCommit(
        ObjectId objectId,
        ImmutableArray<ObjectId> parents)
        => new(
            objectId,
            parents,
            "Author"u8,
            "author@example.invalid"u8,
            DateTimeOffset.UnixEpoch,
            [],
            CommitSignatureStatus.None,
            "subject"u8,
            []);

    private static ObjectId ParseObjectId(string value)
    {
        Assert.IsTrue(ObjectId.TryParseHex(System.Text.Encoding.ASCII.GetBytes(value), out var objectId));
        return objectId!;
    }
}
