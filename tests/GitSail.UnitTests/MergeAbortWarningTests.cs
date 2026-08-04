using GitSail.Domain;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies immutable merge-abort confirmation identity and object-format constraints.
/// </summary>
[TestClass]
public sealed class MergeAbortWarningTests
{
    /// <summary>
    /// Verifies matching includes the optional autostash and rejects empty or mixed-format merge state.
    /// </summary>
    [TestMethod]
    public void Matches_WithExactAndChangedMergeState_UsesEveryGuardedObject()
    {
        var head = ParseObjectId("1111111111111111111111111111111111111111"u8);
        var mergeHead = ParseObjectId("2222222222222222222222222222222222222222"u8);
        var autostash = ParseObjectId("3333333333333333333333333333333333333333"u8);
        var changedAutostash = ParseObjectId("4444444444444444444444444444444444444444"u8);
        var sha256MergeHead = ParseObjectId(
            "5555555555555555555555555555555555555555555555555555555555555555"u8);
        var precondition = new RepositoryPrecondition(
            head,
            RefName.FromBytes("refs/heads/main"u8),
            Enumerable.Repeat((byte)0x5a, 32).ToArray());
        var workTreeFingerprint = Enumerable.Repeat((byte)0x6b, 32).ToArray();
        var changedWorkTreeFingerprint = Enumerable.Repeat((byte)0x7c, 32).ToArray();
        var warning = new MergeAbortWarning(
            precondition,
            [mergeHead],
            workTreeFingerprint,
            autostash);
        var same = new MergeAbortWarning(
            precondition,
            [mergeHead],
            workTreeFingerprint,
            autostash);
        var changed = new MergeAbortWarning(
            precondition,
            [mergeHead],
            workTreeFingerprint,
            changedAutostash);
        var changedWorkTree = new MergeAbortWarning(
            precondition,
            [mergeHead],
            changedWorkTreeFingerprint,
            autostash);

        Assert.IsTrue(warning.Matches(same));
        Assert.IsFalse(warning.Matches(changed));
        Assert.IsFalse(warning.Matches(changedWorkTree));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new MergeAbortWarning(precondition, [], workTreeFingerprint));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new MergeAbortWarning(precondition, [sha256MergeHead], workTreeFingerprint));
    }

    private static ObjectId ParseObjectId(ReadOnlySpan<byte> value)
    {
        Assert.IsTrue(ObjectId.TryParseHex(value, out var objectId));
        return objectId!;
    }
}
