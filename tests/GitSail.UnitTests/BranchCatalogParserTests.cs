using GitSail.Domain;
using GitSail.Git.Execution;
using GitSail.Git.Parsing;
using GitSail.Testing;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies bounded exact parsing of branch refs and linked-worktree porcelain records.
/// </summary>
[TestClass]
public sealed class BranchCatalogParserTests
{
    /// <summary>
    /// Verifies local occupancy, current state, upstream distance, and remote symbolic refs remain distinct.
    /// </summary>
    [TestMethod]
    public void ParseBranches_WithLocalRemoteAndWorktrees_ReturnsExactCatalogRecords()
    {
        var worktreeOutput = new List<byte>();
        AddField(worktreeOutput, "worktree /repository"u8);
        AddField(worktreeOutput, "HEAD 1111111111111111111111111111111111111111"u8);
        AddField(worktreeOutput, "branch refs/heads/main"u8);
        AddField(worktreeOutput, []);
        AddField(worktreeOutput, "worktree /linked/team"u8);
        AddField(worktreeOutput, "HEAD 2222222222222222222222222222222222222222"u8);
        AddField(worktreeOutput, "branch refs/heads/team/topic"u8);
        AddField(worktreeOutput, "locked maintenance window"u8);
        AddField(worktreeOutput, []);
        var worktrees = BranchCatalogParser.ParseWorktrees(worktreeOutput.ToArray());
        var branchOutput = new List<byte>();
        AddBranchRecord(
            branchOutput,
            "refs/heads/main"u8,
            "1111111111111111111111111111111111111111"u8,
            "refs/remotes/origin/main"u8,
            "[ahead 2, behind 3]"u8,
            "*"u8,
            []);
        AddBranchRecord(
            branchOutput,
            "refs/heads/team/topic"u8,
            "2222222222222222222222222222222222222222"u8,
            [],
            [],
            " "u8,
            []);
        AddBranchRecord(
            branchOutput,
            "refs/remotes/origin/HEAD"u8,
            "1111111111111111111111111111111111111111"u8,
            [],
            [],
            " "u8,
            "refs/remotes/origin/main"u8);

        var branches = BranchCatalogParser.ParseBranches(branchOutput.ToArray(), worktrees);

        Assert.HasCount(2, worktrees);
        var linkedPath = OperatingSystem.IsWindows() ? @"\linked\team" : "/linked/team";
        Assert.AreEqual(linkedPath, worktrees[1].Path.DisplayText);
        Assert.IsTrue(worktrees[1].IsLocked);
        Assert.AreEqual("maintenance window", worktrees[1].LockReasonDisplay);
        Assert.HasCount(3, branches);
        var main = branches.Single(static branch => branch.ShortName.DisplayText == "main");
        Assert.AreEqual(BranchKind.Local, main.Kind);
        Assert.IsTrue(main.IsCurrent);
        Assert.AreEqual(2, main.AheadCount);
        Assert.AreEqual(3, main.BehindCount);
        Assert.AreEqual("refs/remotes/origin/main", main.UpstreamName?.DisplayText);
        Assert.HasCount(1, main.OccupiedWorktrees);
        var topic = branches.Single(static branch => branch.ShortName.DisplayText == "team/topic");
        Assert.HasCount(1, topic.OccupiedWorktrees);
        Assert.AreEqual(linkedPath, topic.OccupiedWorktrees[0].DisplayText);
        var remoteHead = branches.Single(static branch => branch.ShortName.DisplayText == "origin/HEAD");
        Assert.AreEqual(BranchKind.RemoteTracking, remoteHead.Kind);
        Assert.AreEqual("refs/remotes/origin/main", remoteHead.SymbolicTarget?.DisplayText);
    }

    /// <summary>
    /// Verifies a detached, prunable worktree retains exact state without inventing a branch attachment.
    /// </summary>
    [TestMethod]
    public void ParseWorktrees_WithDetachedPrunableRecord_ReturnsDetachedState()
    {
        var output = new List<byte>();
        AddField(output, "worktree /missing/worktree"u8);
        AddField(output, "HEAD 3333333333333333333333333333333333333333"u8);
        AddField(output, "detached"u8);
        AddField(output, "prunable gitdir file points to non-existent location"u8);
        AddField(output, []);

        var worktree = TestSeq.Single(BranchCatalogParser.ParseWorktrees(output.ToArray()));

        Assert.IsNull(worktree.BranchName);
        Assert.IsTrue(worktree.IsPrunable);
        Assert.AreEqual("gitdir file points to non-existent location", worktree.PrunableReasonDisplay);
    }

    /// <summary>
    /// Verifies branch records with missing NUL fields fail closed before producing a partial catalog.
    /// </summary>
    [TestMethod]
    public void ParseBranches_WithTruncatedRecord_ThrowsInvalidDataException()
    {
        var output = new List<byte>();
        AddField(output, "refs/heads/main"u8);
        AddField(output, "1111111111111111111111111111111111111111"u8);
        output.Add((byte)'\n');

        Assert.ThrowsExactly<InvalidDataException>(() =>
            BranchCatalogParser.ParseBranches(output.ToArray(), []));
    }

    /// <summary>
    /// Verifies a remote-to-local proposal removes only the remote component and preserves the complete tail.
    /// </summary>
    [TestMethod]
    public void GetLocalNameProposal_WithNestedRemoteBranch_PreservesCompleteTail()
    {
        var remoteBranch = new BranchInfo(
            RefName.FromBytes("refs/remotes/origin/team/feature"u8),
            RefName.FromBytes("origin/team/feature"u8),
            BranchKind.RemoteTracking,
            ParseObjectId("1111111111111111111111111111111111111111"u8),
            upstreamName: null,
            aheadCount: 0,
            behindCount: 0,
            isUpstreamGone: false,
            isCurrent: false,
            [],
            symbolicTarget: null);

        var proposal = BranchService.GetLocalNameProposal(remoteBranch);

        Assert.AreEqual("team/feature", proposal.DisplayText);
    }

    private static ObjectId ParseObjectId(ReadOnlySpan<byte> value)
    {
        Assert.IsTrue(ObjectId.TryParseHex(value, out var objectId));
        return objectId!;
    }

    private static void AddBranchRecord(
        List<byte> output,
        ReadOnlySpan<byte> fullName,
        ReadOnlySpan<byte> objectId,
        ReadOnlySpan<byte> upstream,
        ReadOnlySpan<byte> tracking,
        ReadOnlySpan<byte> head,
        ReadOnlySpan<byte> symbolicTarget)
    {
        AddField(output, fullName);
        AddField(output, objectId);
        AddField(output, upstream);
        AddField(output, tracking);
        AddField(output, head);
        AddField(output, symbolicTarget);
        output.Add((byte)'\n');
    }

    private static void AddField(List<byte> output, ReadOnlySpan<byte> value)
    {
        output.AddRange(value.ToArray());
        output.Add(0);
    }
}
