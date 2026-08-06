using GitSail.Domain;
using Hex1b.Widgets;
using System.Collections.Immutable;

namespace GitSail.Ui;

/// <summary>
/// Owns searchable remote-window data and controlled exact list focus.
/// </summary>
internal sealed class RemoteWorkspaceState
{
    private ImmutableArray<RemoteWorkspaceItem> _allItems = [];
    private RemoteName? _focusedName;
    private readonly TerminalMouseReportFilter _inputFilter = new();

    /// <summary>
    /// Initializes empty remote-window state and its lifted filter editor.
    /// </summary>
    internal RemoteWorkspaceState()
    {
        Filter = new TextBoxState();
    }

    /// <summary>
    /// Gets the stable exact remote catalog currently presented to the user.
    /// </summary>
    internal RemoteCatalog? Catalog { get; private set; }

    /// <summary>
    /// Gets the lifted incremental remote-filter input.
    /// </summary>
    internal TextBoxState Filter { get; }

    /// <summary>
    /// Gets the remote items matching the current control-safe filter.
    /// </summary>
    internal ImmutableArray<RemoteWorkspaceItem> VisibleItems { get; private set; } = [];

    /// <summary>
    /// Gets the controlled cursor index in the filtered remote list.
    /// </summary>
    internal int FocusedIndex => GetFocusedIndex();

    /// <summary>
    /// Gets the exact remote item currently driving details and actions.
    /// </summary>
    internal RemoteWorkspaceItem? FocusedItem
        => VisibleItems.IsEmpty ? null : VisibleItems[GetFocusedIndex()];

    /// <summary>
    /// Replaces the remote catalog while retaining exact name focus when possible.
    /// </summary>
    /// <param name="catalog">The newly captured stable remote catalog.</param>
    internal void ApplyCatalog(RemoteCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Catalog = catalog;
        _allItems = [.. catalog.Remotes.Select(static remote => new RemoteWorkspaceItem(remote))];
        if (_focusedName is null || !_allItems.Any(item => item.Key.Equals(_focusedName)))
        {
            _focusedName = _allItems.FirstOrDefault()?.Key;
        }

        ApplyFilter();
    }

    /// <summary>
    /// Applies incremental matching across exact name and redacted fetch and push URLs.
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
    /// Moves controlled focus to one visible remote row.
    /// </summary>
    /// <param name="index">The absolute filtered row index.</param>
    internal void Focus(int index)
    {
        if (index < 0 || index >= VisibleItems.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        _focusedName = VisibleItems[index].Key;
    }

    /// <summary>
    /// Clears stale catalog data while retaining the user's filter for the next open.
    /// </summary>
    internal void Clear()
    {
        Catalog = null;
        _allItems = [];
        VisibleItems = [];
        _focusedName = null;
    }

    private void ApplyFilter()
    {
        var filter = Filter.Text.Trim();
        VisibleItems = string.IsNullOrEmpty(filter)
            ? _allItems
            : [.. _allItems.Where(item => MatchesFilter(item.Remote, filter))];
        if (_focusedName is null || !VisibleItems.Any(item => item.Key.Equals(_focusedName)))
        {
            _focusedName = VisibleItems.FirstOrDefault()?.Key;
        }
    }

    private int GetFocusedIndex()
    {
        if (_focusedName is not null)
        {
            for (var index = 0; index < VisibleItems.Length; index++)
            {
                if (VisibleItems[index].Key.Equals(_focusedName))
                {
                    return index;
                }
            }
        }

        return 0;
    }

    private static bool MatchesFilter(RemoteInfo remote, string filter)
        => remote.Name.DisplayText.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            remote.FetchUrls.Any(url => url.RedactedDisplayText.Contains(
                filter,
                StringComparison.OrdinalIgnoreCase)) ||
            remote.PushUrls.Any(url => url.RedactedDisplayText.Contains(
                filter,
                StringComparison.OrdinalIgnoreCase));
}
