using GitSail.Domain;
using GitSail.Ui;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies controlled status-pane focus and exact-path selection behavior.
/// </summary>
[TestClass]
public sealed class StatusWorkspaceStateTests
{
    /// <summary>
    /// Verifies that entries appear in every pane whose side contains a change.
    /// </summary>
    [TestMethod]
    public void Constructor_WithMixedStatusEntries_PartitionsBothSidesCorrectly()
    {
        var both = CreateEntry("both.txt", GitFileStatus.Modified, GitFileStatus.Modified);
        var untracked = CreateEntry("new.txt", GitFileStatus.Unmodified, GitFileStatus.Untracked);
        var staged = CreateEntry("added.txt", GitFileStatus.Added, GitFileStatus.Unmodified);

        var state = new StatusWorkspaceState(CreateSnapshot(1, both, untracked, staged));

        Assert.HasCount(2, state.UnstagedItems);
        Assert.HasCount(2, state.StagedItems);
        Assert.AreEqual("M both.txt", state.UnstagedItems[0].ToString());
        Assert.AreEqual("? new.txt", state.UnstagedItems[1].ToString());
        Assert.AreEqual("M both.txt", state.StagedItems[0].ToString());
        Assert.AreEqual("A added.txt", state.StagedItems[1].ToString());
    }

    /// <summary>
    /// Verifies conflict-only scope exposes one unmerged list and omits every ordinary change.
    /// </summary>
    [TestMethod]
    public void Constructor_WithUnmergedScope_PresentsOnlyConflictEntriesOnce()
    {
        var ordinary = CreateEntry("ordinary.txt", GitFileStatus.Modified, GitFileStatus.Modified);
        var conflict = new RepositoryStatusEntry(
            RepositoryStatusEntryKind.Unmerged,
            GitFileStatus.Unmerged,
            GitFileStatus.Unmerged,
            CreatePath("conflict.txt"),
            OriginalPath: null,
            SimilarityPercentage: null,
            IsSubmodule: false);

        var state = new StatusWorkspaceState(
            CreateSnapshot(1, ordinary, conflict),
            StatusWorkspaceScope.UnmergedOnly);

        Assert.HasCount(1, state.UnstagedItems);
        Assert.AreEqual("conflict.txt", state.UnstagedItems[0].Path.DisplayText);
        Assert.IsEmpty(state.StagedItems);
        Assert.AreEqual(StatusWorkspacePane.Unstaged, state.ActivePane);
    }

    /// <summary>
    /// Verifies that a newer reordered generation retains checked and focused raw paths.
    /// </summary>
    [TestMethod]
    public void ApplySnapshot_WithReorderedPaths_PreservesControlledIdentity()
    {
        var first = CreateEntry("first.txt", GitFileStatus.Unmodified, GitFileStatus.Modified);
        var second = CreateEntry("second.txt", GitFileStatus.Unmodified, GitFileStatus.Modified);
        var state = new StatusWorkspaceState(CreateSnapshot(1, first, second));
        state.SetUnstagedSelection([0, 1]);
        state.FocusUnstaged(1);

        state.ApplySnapshot(CreateSnapshot(2, second, first));

        Assert.HasCount(2, state.UnstagedSelectedIndices);
        Assert.AreEqual(0, state.UnstagedSelectedIndices[0]);
        Assert.AreEqual(1, state.UnstagedSelectedIndices[1]);
        Assert.AreEqual(0, state.UnstagedFocusedIndex);
        Assert.AreEqual("second.txt", state.FocusedItem?.Path.DisplayText);
    }

    /// <summary>
    /// Verifies changed-path filtering matches current and original paths without case sensitivity.
    /// </summary>
    [TestMethod]
    public void SetFilter_WithCurrentAndOriginalPaths_FiltersBothPanes()
    {
        var ordinary = CreateEntry("Source/Current.cs", GitFileStatus.Modified, GitFileStatus.Modified);
        var renamed = CreateEntry("new-name.txt", GitFileStatus.Renamed, GitFileStatus.Unmodified) with
        {
            OriginalPath = CreatePath("OLD-NAME.txt"),
        };
        var state = new StatusWorkspaceState(CreateSnapshot(1, ordinary, renamed));

        state.SetFilter("source/current");

        Assert.HasCount(1, state.UnstagedItems);
        Assert.HasCount(1, state.StagedItems);
        Assert.AreEqual(2, state.StagedTotalCount);
        Assert.AreEqual("Source/Current.cs", state.UnstagedItems[0].Path.DisplayText);

        state.SetFilter("old-name");

        Assert.IsEmpty(state.UnstagedItems);
        Assert.HasCount(1, state.StagedItems);
        Assert.AreEqual("new-name.txt", state.StagedItems[0].Path.DisplayText);
    }

    /// <summary>
    /// Verifies hidden checks remain retained but filtered actions never mutate an invisible path.
    /// </summary>
    [TestMethod]
    public void SetFilter_WithHiddenSelection_UsesOnlyVisibleActionPaths()
    {
        var first = CreateEntry("first.txt", GitFileStatus.Unmodified, GitFileStatus.Modified);
        var second = CreateEntry("second.txt", GitFileStatus.Unmodified, GitFileStatus.Modified);
        var state = new StatusWorkspaceState(CreateSnapshot(1, first, second));
        state.SetUnstagedSelection([1]);

        state.SetFilter("first");

        Assert.IsEmpty(state.UnstagedSelectedIndices);
        var visibleActionPaths = state.GetPathsToStage();
        Assert.HasCount(1, visibleActionPaths);
        Assert.AreEqual("first.txt", visibleActionPaths[0].DisplayText);
        state.SetUnstagedSelection([0]);
        state.SetFilter(string.Empty);
        Assert.HasCount(2, state.UnstagedSelectedIndices);
    }

    /// <summary>
    /// Verifies repository refresh retains the active filter and exact visible focused path.
    /// </summary>
    [TestMethod]
    public void ApplySnapshot_WithActiveFilter_PreservesFilterAndVisibleFocus()
    {
        var first = CreateEntry("first.txt", GitFileStatus.Unmodified, GitFileStatus.Modified);
        var second = CreateEntry("second.txt", GitFileStatus.Unmodified, GitFileStatus.Modified);
        var third = CreateEntry("third.txt", GitFileStatus.Unmodified, GitFileStatus.Modified);
        var state = new StatusWorkspaceState(CreateSnapshot(1, first, second));
        state.SetFilter("second");

        state.ApplySnapshot(CreateSnapshot(2, third, second, first));

        Assert.AreEqual("second", state.Filter.Text);
        Assert.HasCount(1, state.UnstagedItems);
        Assert.AreEqual(3, state.UnstagedTotalCount);
        Assert.AreEqual("second.txt", state.FocusedItem?.Path.DisplayText);
    }

    /// <summary>
    /// Verifies that actions use checked paths and otherwise fall back to the focused row.
    /// </summary>
    [TestMethod]
    public void GetPathsToStage_WithAndWithoutChecks_UsesExpectedPaths()
    {
        var first = CreateEntry("first.txt", GitFileStatus.Unmodified, GitFileStatus.Modified);
        var second = CreateEntry("second.txt", GitFileStatus.Unmodified, GitFileStatus.Modified);
        var state = new StatusWorkspaceState(CreateSnapshot(1, first, second));
        state.FocusUnstaged(1);

        var focusedPaths = state.GetPathsToStage();
        state.SetUnstagedSelection([0]);
        var checkedPaths = state.GetPathsToStage();

        Assert.HasCount(1, focusedPaths);
        Assert.AreEqual("second.txt", focusedPaths[0].DisplayText);
        Assert.HasCount(1, checkedPaths);
        Assert.AreEqual("first.txt", checkedPaths[0].DisplayText);
    }

    /// <summary>
    /// Verifies configured tools receive retained checked paths even when a display filter hides them.
    /// </summary>
    [TestMethod]
    public void GetSelectedOrFocusedPaths_WithHiddenChecks_PreservesCompleteSelection()
    {
        var first = CreateEntry("first.txt", GitFileStatus.Unmodified, GitFileStatus.Modified);
        var second = CreateEntry("second.txt", GitFileStatus.Unmodified, GitFileStatus.Modified);
        var state = new StatusWorkspaceState(CreateSnapshot(1, first, second));
        state.SetUnstagedSelection([1]);
        state.SetFilter("first");

        var selectedPaths = state.GetSelectedOrFocusedPaths();

        Assert.HasCount(1, selectedPaths);
        Assert.AreEqual("second.txt", selectedPaths[0].DisplayText);
    }

    /// <summary>
    /// Verifies that an older asynchronous generation cannot replace current workspace state.
    /// </summary>
    [TestMethod]
    public void ApplySnapshot_WithOlderGeneration_IgnoresStaleResult()
    {
        var current = CreateEntry("current.txt", GitFileStatus.Unmodified, GitFileStatus.Modified);
        var stale = CreateEntry("stale.txt", GitFileStatus.Unmodified, GitFileStatus.Modified);
        var state = new StatusWorkspaceState(CreateSnapshot(2, current));

        state.ApplySnapshot(CreateSnapshot(1, stale));

        Assert.AreEqual(2L, state.Snapshot.Generation.Value);
        Assert.AreEqual("current.txt", state.UnstagedItems[0].Path.DisplayText);
    }

    private static RepositoryStatusEntry CreateEntry(
        string path,
        GitFileStatus indexStatus,
        GitFileStatus workTreeStatus)
        => new(
            workTreeStatus == GitFileStatus.Untracked
                ? RepositoryStatusEntryKind.Untracked
                : RepositoryStatusEntryKind.Ordinary,
            indexStatus,
            workTreeStatus,
            CreatePath(path),
            OriginalPath: null,
            SimilarityPercentage: null,
            IsSubmodule: false);

    private static RepositoryStatusSnapshot CreateSnapshot(
        long generation,
        params RepositoryStatusEntry[] entries)
    {
        var root = CreatePath(OperatingSystem.IsWindows() ? "C:\\repository" : "/repository");
        var repository = new RepositoryLocation(
            root,
            root,
            root,
            Prefix: null,
            RepositoryObjectFormat.Sha1,
            IsBare: false);
        return new RepositoryStatusSnapshot(
            new OperationGeneration(generation),
            repository,
            HeadObjectId: null,
            HeadName: null,
            UpstreamName: null,
            AheadCount: 0,
            BehindCount: 0,
            [.. entries]);
    }

    private static GitPath CreatePath(string path)
        => OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath(path)
            : GitPath.FromUnixBytes(System.Text.Encoding.UTF8.GetBytes(path));
}
