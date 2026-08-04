using GitSail.Domain;
using GitSail.Ui;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies controlled linked-worktree filtering, focus retention, and row presentation.
/// </summary>
[TestClass]
public sealed class WorktreeWorkspaceStateTests
{
    /// <summary>
    /// Verifies filtering matches exact paths, branches, lock reasons, and state markers.
    /// </summary>
    /// <param name="filter">The control-safe filter text.</param>
    /// <param name="expectedPathTail">The expected focused path suffix.</param>
    [TestMethod]
    [DataRow("topic", "linked-topic")]
    [DataRow("portable", "linked-topic")]
    [DataRow("prunable", "missing")]
    [DataRow("main", "main")]
    public void SetFilter_WithPathBranchReasonOrMarker_SelectsExpectedItem(
        string filter,
        string expectedPathTail)
    {
        var state = new WorktreeWorkspaceState();
        state.ApplyCatalog(CreateCatalog());

        state.SetFilter(filter);

        Assert.HasCount(1, state.VisibleItems);
        Assert.Contains(expectedPathTail, state.FocusedItem!.Worktree.Path.DisplayText);
    }

    /// <summary>
    /// Verifies exact path focus survives catalog replacement and filter clearing.
    /// </summary>
    [TestMethod]
    public void ApplyCatalog_WithRetainedPath_PreservesFocusedWorktree()
    {
        var state = new WorktreeWorkspaceState();
        var catalog = CreateCatalog();
        state.ApplyCatalog(catalog);
        state.Focus(1);
        var focused = state.FocusedItem!.Key;
        state.SetFilter("linked");

        state.ApplyCatalog(CreateCatalog());
        state.SetFilter(string.Empty);

        Assert.IsTrue(state.FocusedItem!.Key.Equals(focused));
        Assert.AreEqual(1, state.FocusedIndex);
    }

    /// <summary>
    /// Verifies list rows expose the safe path, branch, main, lock, and prune markers.
    /// </summary>
    [TestMethod]
    public void ToString_WithEveryState_FormatsExpectedMarkers()
    {
        var state = new WorktreeWorkspaceState();
        state.ApplyCatalog(CreateCatalog());

        Assert.Contains("main", state.VisibleItems[0].ToString());
        Assert.Contains("locked", state.VisibleItems[1].ToString());
        Assert.Contains("refs/heads/topic", state.VisibleItems[1].ToString());
        Assert.Contains("prunable", state.VisibleItems[2].ToString());
        Assert.Contains("detached", state.VisibleItems[2].ToString());
    }

    private static BranchCatalog CreateCatalog()
    {
        Assert.IsTrue(ObjectId.TryParseHex(
            "1111111111111111111111111111111111111111"u8,
            out var objectId));
        var mainPath = CreatePath("main");
        var linkedPath = CreatePath("linked-topic");
        var missingPath = CreatePath("missing");
        var worktrees = new[]
        {
            new WorktreeInfo(
                mainPath,
                objectId,
                RefName.FromBytes("refs/heads/main"u8),
                isBare: false,
                isLocked: false,
                lockReasonDisplay: null,
                isPrunable: false,
                prunableReasonDisplay: null),
            new WorktreeInfo(
                linkedPath,
                objectId,
                RefName.FromBytes("refs/heads/topic"u8),
                isBare: false,
                isLocked: true,
                lockReasonDisplay: "portable volume",
                isPrunable: false,
                prunableReasonDisplay: null),
            new WorktreeInfo(
                missingPath,
                objectId,
                branchName: null,
                isBare: false,
                isLocked: false,
                lockReasonDisplay: null,
                isPrunable: true,
                prunableReasonDisplay: "missing directory"),
        };
        var precondition = new RepositoryPrecondition(objectId, RefName.FromBytes("refs/heads/main"u8), new byte[32]);
        return new BranchCatalog(precondition, [], [.. worktrees]);
    }

    private static GitPath CreatePath(string name)
        => OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath($"C:\\repository\\{name}")
            : GitPath.FromUnixBytes(System.Text.Encoding.UTF8.GetBytes($"/repository/{name}"));
}
