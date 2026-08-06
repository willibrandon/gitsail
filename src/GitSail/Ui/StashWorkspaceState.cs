using GitSail.Domain;
using Hex1b.Documents;
using Hex1b.Widgets;
using System.Collections.Immutable;

namespace GitSail.Ui;

/// <summary>
/// Owns searchable stash data, controlled focus, and a lifted read-only patch preview.
/// </summary>
internal sealed class StashWorkspaceState
{
    private ImmutableArray<StashWorkspaceItem> _allItems = [];
    private StashIdentity? _focusedIdentity;
    private readonly TerminalMouseReportFilter _inputFilter = new();

    /// <summary>
    /// Initializes empty stash-window state with lifted filter and preview editors.
    /// </summary>
    internal StashWorkspaceState()
    {
        Filter = new TextBoxState();
        Preview = CreatePreview("Select a stash to inspect its exact patch.");
        PreviewDecorationProvider = new GitSailDiffDecorationProvider();
    }

    /// <summary>
    /// Gets the stable exact stash catalog currently presented to the user.
    /// </summary>
    internal StashCatalog? Catalog { get; private set; }

    /// <summary>
    /// Gets the lifted incremental stash-filter input.
    /// </summary>
    internal TextBoxState Filter { get; }

    /// <summary>
    /// Gets the stash items matching the current control-safe filter.
    /// </summary>
    internal ImmutableArray<StashWorkspaceItem> VisibleItems { get; private set; } = [];

    /// <summary>
    /// Gets the controlled cursor index in the filtered stash list.
    /// </summary>
    internal int FocusedIndex => GetFocusedIndex();

    /// <summary>
    /// Gets the exact stash item currently driving details, preview, and actions.
    /// </summary>
    internal StashWorkspaceItem? FocusedItem
        => VisibleItems.IsEmpty ? null : VisibleItems[GetFocusedIndex()];

    /// <summary>
    /// Gets the lifted read-only patch preview editor.
    /// </summary>
    internal EditorState Preview { get; private set; }

    /// <summary>
    /// Gets the decoration provider owned by the current stash preview document.
    /// </summary>
    internal ITextDecorationProvider PreviewDecorationProvider { get; private set; }

    /// <summary>
    /// Gets the control-safe title for the current stash patch preview.
    /// </summary>
    internal string PreviewTitle { get; private set; } = "Stash patch";

    /// <summary>
    /// Replaces the stash catalog while retaining exact object focus when possible.
    /// </summary>
    /// <param name="catalog">The newly captured stable stash catalog.</param>
    internal void ApplyCatalog(StashCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Catalog = catalog;
        _allItems = [.. catalog.Entries.Select(static stash => new StashWorkspaceItem(stash))];
        if (_focusedIdentity is not null && !_allItems.Any(item => item.Key.Equals(_focusedIdentity)))
        {
            _focusedIdentity = _allItems.FirstOrDefault(
                item => item.Stash.ObjectId.Equals(_focusedIdentity.ObjectId))?.Key;
        }

        _focusedIdentity ??= _allItems.FirstOrDefault()?.Key;
        ApplyFilter();
    }

    /// <summary>
    /// Applies incremental matching across selector, object identifier, timestamp, and subject.
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
    /// Moves controlled focus to one visible stash row.
    /// </summary>
    /// <param name="index">The absolute filtered row index.</param>
    internal void Focus(int index)
    {
        if (index < 0 || index >= VisibleItems.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        _focusedIdentity = VisibleItems[index].Key;
    }

    /// <summary>
    /// Replaces the read-only preview for one exact focused stash entry.
    /// </summary>
    /// <param name="stash">The exact entry whose patch was captured.</param>
    /// <param name="text">The control-safe patch presentation.</param>
    internal void SetPreview(StashInfo stash, string text)
    {
        ArgumentNullException.ThrowIfNull(stash);
        ArgumentNullException.ThrowIfNull(text);
        PreviewTitle = $"Patch: {stash.Selector} {stash.ObjectId.ToString()[..12]}";
        Preview = CreatePreview(text);
        PreviewDecorationProvider = new GitSailDiffDecorationProvider();
    }

    /// <summary>
    /// Replaces the patch pane with a control-safe non-entry status message.
    /// </summary>
    /// <param name="text">The empty, loading, or failure presentation text.</param>
    internal void SetPreviewMessage(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        PreviewTitle = "Stash patch";
        Preview = CreatePreview(text);
        PreviewDecorationProvider = new GitSailDiffDecorationProvider();
    }

    /// <summary>
    /// Clears stale catalog and preview data while retaining the user's filter.
    /// </summary>
    internal void Clear()
    {
        Catalog = null;
        _allItems = [];
        VisibleItems = [];
        _focusedIdentity = null;
        PreviewTitle = "Stash patch";
        Preview = CreatePreview("Reload stashes to inspect an exact patch.");
        PreviewDecorationProvider = new GitSailDiffDecorationProvider();
    }

    private void ApplyFilter()
    {
        var filter = Filter.Text.Trim();
        VisibleItems = string.IsNullOrEmpty(filter)
            ? _allItems
            : [.. _allItems.Where(item => MatchesFilter(item.Stash, filter))];
        if (_focusedIdentity is null || !VisibleItems.Any(item => item.Key.Equals(_focusedIdentity)))
        {
            _focusedIdentity = VisibleItems.FirstOrDefault()?.Key;
        }
    }

    private int GetFocusedIndex()
    {
        if (_focusedIdentity is not null)
        {
            for (var index = 0; index < VisibleItems.Length; index++)
            {
                if (VisibleItems[index].Key.Equals(_focusedIdentity))
                {
                    return index;
                }
            }
        }

        return 0;
    }

    private static bool MatchesFilter(StashInfo stash, string filter)
        => stash.Selector.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            stash.ObjectId.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            stash.DisplayMessage.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            stash.CreatedAt.ToLocalTime().ToString("g").Contains(filter, StringComparison.OrdinalIgnoreCase);

    private static EditorState CreatePreview(string text)
        => new(new Hex1bDocument(text))
        {
            IsReadOnly = true,
        };
}
