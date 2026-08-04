using GitSail.Domain;
using System.Collections.Immutable;

namespace GitSail.Ui;

/// <summary>
/// Owns controlled file-pane focus and selection for one repository status snapshot.
/// </summary>
internal sealed class StatusWorkspaceState
{
    private readonly HashSet<GitPath> _unstagedSelection = [];
    private readonly HashSet<GitPath> _stagedSelection = [];
    private GitPath? _unstagedFocus;
    private GitPath? _stagedFocus;
    private GitPath? _unstagedSelectionAnchor;
    private GitPath? _stagedSelectionAnchor;
    private StatusWorkspacePane _activePane = StatusWorkspacePane.Unstaged;

    /// <summary>
    /// Initializes controlled status state from the first repository snapshot.
    /// </summary>
    /// <param name="snapshot">The initial complete status snapshot.</param>
    internal StatusWorkspaceState(RepositoryStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Snapshot = snapshot;
        UnstagedItems = CreateUnstagedItems(snapshot);
        StagedItems = CreateStagedItems(snapshot);
        _unstagedFocus = UnstagedItems.FirstOrDefault()?.Path;
        _stagedFocus = StagedItems.FirstOrDefault()?.Path;
        if (UnstagedItems.Length == 0 && StagedItems.Length > 0)
        {
            _activePane = StatusWorkspacePane.Staged;
        }
    }

    /// <summary>
    /// Gets the current immutable repository status snapshot.
    /// </summary>
    internal RepositoryStatusSnapshot Snapshot { get; private set; }

    /// <summary>
    /// Gets the current worktree and untracked pane items.
    /// </summary>
    internal ImmutableArray<StatusWorkspaceItem> UnstagedItems { get; private set; }

    /// <summary>
    /// Gets the current index pane items.
    /// </summary>
    internal ImmutableArray<StatusWorkspaceItem> StagedItems { get; private set; }

    /// <summary>
    /// Gets checked worktree row indices for the controlled multi-select list.
    /// </summary>
    internal IReadOnlyList<int> UnstagedSelectedIndices => GetSelectedIndices(UnstagedItems, _unstagedSelection);

    /// <summary>
    /// Gets checked index row indices for the controlled multi-select list.
    /// </summary>
    internal IReadOnlyList<int> StagedSelectedIndices => GetSelectedIndices(StagedItems, _stagedSelection);

    /// <summary>
    /// Gets the controlled worktree list cursor index.
    /// </summary>
    internal int UnstagedFocusedIndex => GetFocusedIndex(UnstagedItems, _unstagedFocus);

    /// <summary>
    /// Gets the controlled index list cursor index.
    /// </summary>
    internal int StagedFocusedIndex => GetFocusedIndex(StagedItems, _stagedFocus);

    /// <summary>
    /// Gets the item currently driving the selected-path details pane.
    /// </summary>
    internal StatusWorkspaceItem? FocusedItem
        => _activePane == StatusWorkspacePane.Unstaged
            ? GetFocusedItem(UnstagedItems, _unstagedFocus)
            : GetFocusedItem(StagedItems, _stagedFocus);

    /// <summary>
    /// Replaces the snapshot while retaining valid focus and checked paths by exact identity.
    /// </summary>
    /// <param name="snapshot">The newer complete repository status generation.</param>
    internal void ApplySnapshot(RepositoryStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Generation.CompareTo(Snapshot.Generation) < 0)
        {
            return;
        }

        Snapshot = snapshot;
        UnstagedItems = CreateUnstagedItems(snapshot);
        StagedItems = CreateStagedItems(snapshot);
        IntersectSelection(_unstagedSelection, UnstagedItems);
        IntersectSelection(_stagedSelection, StagedItems);
        _unstagedFocus = RetainFocus(_unstagedFocus, UnstagedItems);
        _stagedFocus = RetainFocus(_stagedFocus, StagedItems);
        if (_activePane == StatusWorkspacePane.Unstaged && UnstagedItems.Length == 0 && StagedItems.Length > 0)
        {
            _activePane = StatusWorkspacePane.Staged;
        }
        else if (_activePane == StatusWorkspacePane.Staged && StagedItems.Length == 0 && UnstagedItems.Length > 0)
        {
            _activePane = StatusWorkspacePane.Unstaged;
        }
    }

    /// <summary>
    /// Replaces checked worktree paths from the list's authoritative selected indices.
    /// </summary>
    /// <param name="indices">The complete selected-index set after an interaction.</param>
    /// <param name="anchorIndex">The range-selection anchor, or a negative value to retain it.</param>
    internal void SetUnstagedSelection(IReadOnlyList<int> indices, int anchorIndex = -1)
    {
        ReplaceSelection(_unstagedSelection, UnstagedItems, indices);
        if (anchorIndex >= 0)
        {
            _unstagedSelectionAnchor = GetPathAt(UnstagedItems, anchorIndex);
        }
    }

    /// <summary>
    /// Replaces checked index paths from the list's authoritative selected indices.
    /// </summary>
    /// <param name="indices">The complete selected-index set after an interaction.</param>
    /// <param name="anchorIndex">The range-selection anchor, or a negative value to retain it.</param>
    internal void SetStagedSelection(IReadOnlyList<int> indices, int anchorIndex = -1)
    {
        ReplaceSelection(_stagedSelection, StagedItems, indices);
        if (anchorIndex >= 0)
        {
            _stagedSelectionAnchor = GetPathAt(StagedItems, anchorIndex);
        }
    }

    /// <summary>
    /// Toggles one worktree row from a Ctrl-click and makes it the range anchor.
    /// </summary>
    /// <param name="index">The absolute worktree row index under the pointer.</param>
    internal void ToggleUnstagedSelection(int index)
        => ToggleSelection(
            UnstagedItems,
            _unstagedSelection,
            index,
            ref _unstagedFocus,
            ref _unstagedSelectionAnchor,
            StatusWorkspacePane.Unstaged);

    /// <summary>
    /// Extends worktree selection from its anchor through one Shift-clicked row.
    /// </summary>
    /// <param name="index">The absolute worktree row index under the pointer.</param>
    internal void ExtendUnstagedSelection(int index)
        => ExtendSelection(
            UnstagedItems,
            _unstagedSelection,
            index,
            ref _unstagedFocus,
            ref _unstagedSelectionAnchor,
            StatusWorkspacePane.Unstaged);

    /// <summary>
    /// Toggles one index row from a Ctrl-click and makes it the range anchor.
    /// </summary>
    /// <param name="index">The absolute index row under the pointer.</param>
    internal void ToggleStagedSelection(int index)
        => ToggleSelection(
            StagedItems,
            _stagedSelection,
            index,
            ref _stagedFocus,
            ref _stagedSelectionAnchor,
            StatusWorkspacePane.Staged);

    /// <summary>
    /// Extends index selection from its anchor through one Shift-clicked row.
    /// </summary>
    /// <param name="index">The absolute index row under the pointer.</param>
    internal void ExtendStagedSelection(int index)
        => ExtendSelection(
            StagedItems,
            _stagedSelection,
            index,
            ref _stagedFocus,
            ref _stagedSelectionAnchor,
            StatusWorkspacePane.Staged);

    /// <summary>
    /// Moves active detail focus to one worktree pane row.
    /// </summary>
    /// <param name="index">The focused row index.</param>
    internal void FocusUnstaged(int index)
    {
        _unstagedFocus = GetPathAt(UnstagedItems, index);
        _activePane = StatusWorkspacePane.Unstaged;
    }

    /// <summary>
    /// Moves active detail focus to one index pane row.
    /// </summary>
    /// <param name="index">The focused row index.</param>
    internal void FocusStaged(int index)
    {
        _stagedFocus = GetPathAt(StagedItems, index);
        _activePane = StatusWorkspacePane.Staged;
    }

    /// <summary>
    /// Gets exact worktree paths to stage from checked rows or the focused fallback row.
    /// </summary>
    /// <returns>The non-duplicated path collection in visible row order.</returns>
    internal IReadOnlyList<GitPath> GetPathsToStage()
        => GetActionPaths(UnstagedItems, _unstagedSelection, _unstagedFocus);

    /// <summary>
    /// Gets exact index paths to unstage from checked rows or the focused fallback row.
    /// </summary>
    /// <returns>The non-duplicated path collection in visible row order.</returns>
    internal IReadOnlyList<GitPath> GetPathsToUnstage()
        => GetActionPaths(StagedItems, _stagedSelection, _stagedFocus);

    private static ImmutableArray<StatusWorkspaceItem> CreateUnstagedItems(RepositoryStatusSnapshot snapshot)
        => [.. snapshot.Entries
            .Where(static entry => entry.WorkTreeStatus is not (GitFileStatus.Unmodified or GitFileStatus.Ignored))
            .Select(static entry => new StatusWorkspaceItem(entry, entry.WorkTreeStatus))];

    private static ImmutableArray<StatusWorkspaceItem> CreateStagedItems(RepositoryStatusSnapshot snapshot)
        => [.. snapshot.Entries
            .Where(static entry => entry.IndexStatus != GitFileStatus.Unmodified)
            .Select(static entry => new StatusWorkspaceItem(entry, entry.IndexStatus))];

    private static List<int> GetSelectedIndices(
        ImmutableArray<StatusWorkspaceItem> items,
        HashSet<GitPath> selection)
    {
        var result = new List<int>(selection.Count);
        for (var index = 0; index < items.Length; index++)
        {
            if (selection.Contains(items[index].Path))
            {
                result.Add(index);
            }
        }

        return result;
    }

    private static int GetFocusedIndex(ImmutableArray<StatusWorkspaceItem> items, GitPath? focusedPath)
    {
        if (focusedPath is not null)
        {
            for (var index = 0; index < items.Length; index++)
            {
                if (items[index].Path.Equals(focusedPath))
                {
                    return index;
                }
            }
        }

        return 0;
    }

    private static StatusWorkspaceItem? GetFocusedItem(
        ImmutableArray<StatusWorkspaceItem> items,
        GitPath? focusedPath)
    {
        if (focusedPath is not null)
        {
            foreach (var item in items)
            {
                if (item.Path.Equals(focusedPath))
                {
                    return item;
                }
            }
        }

        return items.FirstOrDefault();
    }

    private static void IntersectSelection(
        HashSet<GitPath> selection,
        ImmutableArray<StatusWorkspaceItem> items)
    {
        var available = items.Select(static item => item.Path).ToHashSet();
        selection.IntersectWith(available);
    }

    private static GitPath? RetainFocus(GitPath? focusedPath, ImmutableArray<StatusWorkspaceItem> items)
        => focusedPath is not null && items.Any(item => item.Path.Equals(focusedPath))
            ? focusedPath
            : items.FirstOrDefault()?.Path;

    private static void ReplaceSelection(
        HashSet<GitPath> destination,
        ImmutableArray<StatusWorkspaceItem> items,
        IReadOnlyList<int> indices)
    {
        ArgumentNullException.ThrowIfNull(indices);
        destination.Clear();
        foreach (var index in indices)
        {
            destination.Add(GetPathAt(items, index));
        }
    }

    private static GitPath GetPathAt(ImmutableArray<StatusWorkspaceItem> items, int index)
    {
        if ((uint)index >= (uint)items.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return items[index].Path;
    }

    private static IReadOnlyList<GitPath> GetActionPaths(
        ImmutableArray<StatusWorkspaceItem> items,
        HashSet<GitPath> selection,
        GitPath? focusedPath)
    {
        if (selection.Count > 0)
        {
            return [.. items.Where(item => selection.Contains(item.Path)).Select(static item => item.Path)];
        }

        return focusedPath is null ? [] : [focusedPath];
    }

    private void ToggleSelection(
        ImmutableArray<StatusWorkspaceItem> items,
        HashSet<GitPath> selection,
        int index,
        ref GitPath? focusedPath,
        ref GitPath? anchorPath,
        StatusWorkspacePane pane)
    {
        var path = GetPathAt(items, index);
        if (!selection.Remove(path))
        {
            selection.Add(path);
        }

        focusedPath = path;
        anchorPath = path;
        _activePane = pane;
    }

    private void ExtendSelection(
        ImmutableArray<StatusWorkspaceItem> items,
        HashSet<GitPath> selection,
        int index,
        ref GitPath? focusedPath,
        ref GitPath? anchorPath,
        StatusWorkspacePane pane)
    {
        var targetPath = GetPathAt(items, index);
        var anchorIndex = GetFocusedIndex(items, anchorPath ?? focusedPath);
        var start = Math.Min(anchorIndex, index);
        var end = Math.Max(anchorIndex, index);
        for (var selectedIndex = start; selectedIndex <= end; selectedIndex++)
        {
            selection.Add(items[selectedIndex].Path);
        }

        anchorPath ??= items[anchorIndex].Path;
        focusedPath = targetPath;
        _activePane = pane;
    }
}
