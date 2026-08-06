using GitSail.Domain;
using Hex1b.Widgets;
using System.Collections.Immutable;

namespace GitSail.Ui;

/// <summary>
/// Owns searchable worktree-window data and controlled exact list focus.
/// </summary>
internal sealed class WorktreeWorkspaceState
{
    private ImmutableArray<WorktreeWorkspaceItem> _allItems = [];
    private GitPath? _focusedPath;
    private readonly TerminalMouseReportFilter _inputFilter = new();

    /// <summary>
    /// Initializes empty worktree-window state and its lifted filter editor.
    /// </summary>
    internal WorktreeWorkspaceState()
    {
        Filter = new TextBoxState();
    }

    /// <summary>
    /// Gets the stable exact branch and worktree catalog currently presented.
    /// </summary>
    internal BranchCatalog? Catalog { get; private set; }

    /// <summary>
    /// Gets the lifted incremental worktree-filter input.
    /// </summary>
    internal TextBoxState Filter { get; }

    /// <summary>
    /// Gets the worktree items matching the current control-safe filter.
    /// </summary>
    internal ImmutableArray<WorktreeWorkspaceItem> VisibleItems { get; private set; } = [];

    /// <summary>
    /// Gets the controlled cursor index in the filtered worktree list.
    /// </summary>
    internal int FocusedIndex => GetFocusedIndex();

    /// <summary>
    /// Gets the exact worktree item currently driving details and actions.
    /// </summary>
    internal WorktreeWorkspaceItem? FocusedItem
        => VisibleItems.IsEmpty ? null : VisibleItems[GetFocusedIndex()];

    /// <summary>
    /// Replaces the worktree catalog while retaining exact focus when possible.
    /// </summary>
    /// <param name="catalog">The newly captured stable branch and worktree catalog.</param>
    internal void ApplyCatalog(BranchCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Catalog = catalog;
        _allItems = [.. catalog.Worktrees.Select((worktree, index) =>
            new WorktreeWorkspaceItem(worktree, isMain: index == 0))];
        if (_focusedPath is null || !_allItems.Any(item => item.Key.Equals(_focusedPath)))
        {
            _focusedPath = _allItems.FirstOrDefault()?.Key;
        }

        ApplyFilter();
    }

    /// <summary>
    /// Applies incremental control-safe matching across paths, branches, and state reasons.
    /// </summary>
    /// <param name="filter">The latest user-entered filter text.</param>
    internal void SetFilter(string filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        filter = _inputFilter.Filter(filter);
        if (!string.Equals(Filter.Text, filter, StringComparison.Ordinal))
        {
            Filter.Text = filter;
        }

        ApplyFilter();
    }

    /// <summary>
    /// Moves controlled focus to one visible worktree row.
    /// </summary>
    /// <param name="index">The absolute filtered row index.</param>
    internal void Focus(int index)
    {
        if (index < 0 || index >= VisibleItems.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        _focusedPath = VisibleItems[index].Key;
    }

    /// <summary>
    /// Clears stale catalog data while retaining the user's filter for the next open.
    /// </summary>
    internal void Clear()
    {
        Catalog = null;
        _allItems = [];
        VisibleItems = [];
        _focusedPath = null;
    }

    private void ApplyFilter()
    {
        var filter = Filter.Text.Trim();
        VisibleItems = string.IsNullOrEmpty(filter)
            ? _allItems
            : [.. _allItems.Where(item => MatchesFilter(item, filter))];
        if (_focusedPath is null || !VisibleItems.Any(item => item.Key.Equals(_focusedPath)))
        {
            _focusedPath = VisibleItems.FirstOrDefault()?.Key;
        }
    }

    private int GetFocusedIndex()
    {
        if (_focusedPath is not null)
        {
            for (var index = 0; index < VisibleItems.Length; index++)
            {
                if (VisibleItems[index].Key.Equals(_focusedPath))
                {
                    return index;
                }
            }
        }

        return 0;
    }

    private static bool MatchesFilter(WorktreeWorkspaceItem item, string filter)
        => item.Worktree.Path.DisplayText.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            (item.Worktree.BranchName?.DisplayText.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (item.Worktree.LockReasonDisplay?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (item.Worktree.PrunableReasonDisplay?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (item.IsMain && "main".Contains(filter, StringComparison.OrdinalIgnoreCase)) ||
            (item.Worktree.IsLocked && "locked".Contains(filter, StringComparison.OrdinalIgnoreCase)) ||
            (item.Worktree.IsPrunable && "prunable".Contains(filter, StringComparison.OrdinalIgnoreCase));
}
