using GitSail.Domain;
using GitSail.Ui;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies controlled searchable branch-window focus and display state.
/// </summary>
[TestClass]
public sealed class BranchWorkspaceStateTests
{
    /// <summary>
    /// Verifies current-branch focus, case-insensitive filtering, and exact ref focus retention.
    /// </summary>
    [TestMethod]
    public void ApplyCatalogAndSetFilter_WithNestedBranches_RetainsExactControlledFocus()
    {
        var state = new BranchWorkspaceState();
        var main = CreateBranch("refs/heads/main", BranchKind.Local, isCurrent: true);
        var feature = CreateBranch("refs/heads/team/feature", BranchKind.Local, isCurrent: false);
        var remote = CreateBranch(
            "refs/remotes/origin/team/feature",
            BranchKind.RemoteTracking,
            isCurrent: false);

        state.ApplyCatalog(CreateCatalog(main, feature, remote));

        Assert.AreEqual("main", state.FocusedItem?.Branch.ShortName.DisplayText);
        Assert.HasCount(3, state.VisibleItems);

        state.SetFilter("ORIGIN/TEAM");

        Assert.HasCount(1, state.VisibleItems);
        Assert.AreEqual("origin/team/feature", state.FocusedItem?.Branch.ShortName.DisplayText);
        state.SetFilter(string.Empty);
        Assert.AreEqual("origin/team/feature", state.FocusedItem?.Branch.ShortName.DisplayText);

        state.ApplyCatalog(CreateCatalog(remote, main, feature));

        Assert.AreEqual("origin/team/feature", state.FocusedItem?.Branch.ShortName.DisplayText);
    }

    /// <summary>
    /// Verifies row text presents current, tracking, and worktree-occupancy cues without replacing exact identity.
    /// </summary>
    [TestMethod]
    public void ToString_WithTrackingAndOccupancy_ReturnsActionableControlSafeRow()
    {
        var branch = new BranchInfo(
            RefName.FromBytes("refs/heads/main"u8),
            RefName.FromBytes("main"u8),
            BranchKind.Local,
            ParseObjectId(),
            RefName.FromBytes("refs/remotes/origin/main"u8),
            aheadCount: 2,
            behindCount: 3,
            isUpstreamGone: false,
            isCurrent: true,
            [CreatePath("/repository")],
            symbolicTarget: null);

        var text = new BranchWorkspaceItem(branch).ToString();

        StringAssert.StartsWith(text, "* local");
        StringAssert.Contains(text, "main");
        StringAssert.Contains(text, "ahead 2, behind 3");
        StringAssert.Contains(text, "worktrees: 1");
    }

    private static BranchCatalog CreateCatalog(params BranchInfo[] branches)
        => new(
            new RepositoryPrecondition(
                ParseObjectId(),
                RefName.FromBytes("refs/heads/main"u8),
                new byte[32]),
            [.. branches],
            []);

    private static BranchInfo CreateBranch(string fullName, BranchKind kind, bool isCurrent)
    {
        ReadOnlySpan<byte> prefix = kind == BranchKind.Local ? "refs/heads/"u8 : "refs/remotes/"u8;
        var fullNameBytes = System.Text.Encoding.UTF8.GetBytes(fullName);
        return new BranchInfo(
            RefName.FromBytes(fullNameBytes),
            RefName.FromBytes(fullNameBytes.AsSpan(prefix.Length)),
            kind,
            ParseObjectId(),
            upstreamName: null,
            aheadCount: 0,
            behindCount: 0,
            isUpstreamGone: false,
            isCurrent,
            isCurrent ? [CreatePath("/repository")] : [],
            symbolicTarget: null);
    }

    private static ObjectId ParseObjectId()
    {
        Assert.IsTrue(ObjectId.TryParseHex(
            "1111111111111111111111111111111111111111"u8,
            out var objectId));
        return objectId!;
    }

    private static GitPath CreatePath(string path)
        => OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath(path.Replace('/', '\\'))
            : GitPath.FromUnixBytes(System.Text.Encoding.UTF8.GetBytes(path));
}
