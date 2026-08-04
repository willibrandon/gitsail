using GitSail.Domain;
using Hex1b.Widgets;
using System.Collections.Immutable;

namespace GitSail.Ui;

/// <summary>
/// Owns searchable branch-window data and controlled exact list focus.
/// </summary>
internal sealed class BranchWorkspaceState
{
    private ImmutableArray<BranchWorkspaceItem> _allItems = [];
    private RefName? _focusedRef;

    /// <summary>
    /// Initializes empty branch-window state and its lifted filter editor.
    /// </summary>
    internal BranchWorkspaceState()
    {
        Filter = new TextBoxState();
    }

    /// <summary>
    /// Gets the stable exact branch catalog currently presented to the user.
    /// </summary>
    internal BranchCatalog? Catalog { get; private set; }

    /// <summary>
    /// Gets the lifted incremental branch-filter input.
    /// </summary>
    internal TextBoxState Filter { get; }

    /// <summary>
    /// Gets the branch items matching the current control-safe filter.
    /// </summary>
    internal ImmutableArray<BranchWorkspaceItem> VisibleItems { get; private set; } = [];

    /// <summary>
    /// Gets the controlled cursor index in the filtered branch list.
    /// </summary>
    internal int FocusedIndex => GetFocusedIndex();

    /// <summary>
    /// Gets the exact branch item currently driving details and actions.
    /// </summary>
    internal BranchWorkspaceItem? FocusedItem
        => VisibleItems.IsEmpty ? null : VisibleItems[GetFocusedIndex()];

    /// <summary>
    /// Replaces the branch catalog while retaining exact focus when possible.
    /// </summary>
    /// <param name="catalog">The newly captured stable branch catalog.</param>
    internal void ApplyCatalog(BranchCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Catalog = catalog;
        _allItems = [.. catalog.Branches.Select(static branch => new BranchWorkspaceItem(branch))];
        if (_focusedRef is null || !_allItems.Any(item => item.Key.Equals(_focusedRef)))
        {
            _focusedRef = _allItems.FirstOrDefault(static item => item.Branch.IsCurrent)?.Key ??
                _allItems.FirstOrDefault()?.Key;
        }

        ApplyFilter();
    }

    /// <summary>
    /// Applies incremental control-safe matching across branch and upstream names.
    /// </summary>
    /// <param name="filter">The latest user-entered filter text.</param>
    internal void SetFilter(string filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (!string.Equals(Filter.Text, filter, StringComparison.Ordinal))
        {
            Filter.Text = filter;
        }

        ApplyFilter();
    }

    /// <summary>
    /// Moves controlled focus to one visible branch row.
    /// </summary>
    /// <param name="index">The absolute filtered row index.</param>
    internal void Focus(int index)
    {
        if (index < 0 || index >= VisibleItems.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        _focusedRef = VisibleItems[index].Key;
    }

    /// <summary>
    /// Clears stale catalog data while retaining the user's filter for the next open.
    /// </summary>
    internal void Clear()
    {
        Catalog = null;
        _allItems = [];
        VisibleItems = [];
        _focusedRef = null;
    }

    private void ApplyFilter()
    {
        var filter = Filter.Text.Trim();
        VisibleItems = string.IsNullOrEmpty(filter)
            ? _allItems
            : [.. _allItems.Where(item => MatchesFilter(item.Branch, filter))];
        if (_focusedRef is null || !VisibleItems.Any(item => item.Key.Equals(_focusedRef)))
        {
            _focusedRef = VisibleItems.FirstOrDefault()?.Key;
        }
    }

    private int GetFocusedIndex()
    {
        if (_focusedRef is not null)
        {
            for (var index = 0; index < VisibleItems.Length; index++)
            {
                if (VisibleItems[index].Key.Equals(_focusedRef))
                {
                    return index;
                }
            }
        }

        return 0;
    }

    private static bool MatchesFilter(BranchInfo branch, string filter)
        => branch.ShortName.DisplayText.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            branch.FullName.DisplayText.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            (branch.UpstreamName?.DisplayText.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);
}
