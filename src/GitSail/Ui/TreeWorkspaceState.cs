using GitSail.Domain;
using Hex1b.Documents;
using Hex1b.Widgets;
using System.Collections.Immutable;

namespace GitSail.Ui;

/// <summary>
/// Owns an exact tree listing, controlled filtering and focus, and a lifted object preview.
/// </summary>
internal sealed class TreeWorkspaceState
{
    private readonly TerminalMouseReportFilter _inputFilter = new();
    private ImmutableArray<TreeWorkspaceItem> _allItems = [];
    private TreeEntry? _focusedEntry;

    /// <summary>
    /// Initializes empty tree-browser state with lifted revision, filter, and preview controls.
    /// </summary>
    /// <param name="revision">The initial literal revision text.</param>
    internal TreeWorkspaceState(string revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        Revision = new TextBoxState { Text = revision };
        Filter = new TextBoxState();
        Preview = CreatePreview("Select a tree entry to inspect its exact object.");
    }

    /// <summary>
    /// Gets the lifted revision input.
    /// </summary>
    internal TextBoxState Revision { get; }

    /// <summary>
    /// Gets the lifted incremental tree-filter input.
    /// </summary>
    internal TextBoxState Filter { get; }

    /// <summary>
    /// Gets the current exact tree catalog.
    /// </summary>
    internal TreeCatalog? Catalog { get; private set; }

    /// <summary>
    /// Gets the tree rows matching the current control-safe filter.
    /// </summary>
    internal ImmutableArray<TreeWorkspaceItem> VisibleItems { get; private set; } = [];

    /// <summary>
    /// Gets the controlled cursor index in the filtered tree list.
    /// </summary>
    internal int FocusedIndex => GetFocusedIndex();

    /// <summary>
    /// Gets the exact tree entry currently driving details, navigation, and preview.
    /// </summary>
    internal TreeWorkspaceItem? FocusedItem
        => VisibleItems.IsEmpty ? null : VisibleItems[GetFocusedIndex()];

    /// <summary>
    /// Gets the lifted read-only object preview editor.
    /// </summary>
    internal EditorState Preview { get; private set; }

    /// <summary>
    /// Gets the control-safe title for the current object preview.
    /// </summary>
    internal string PreviewTitle { get; private set; } = "Object preview";

    /// <summary>
    /// Replaces the tree catalog while retaining exact entry focus when possible.
    /// </summary>
    /// <param name="catalog">The newly captured exact tree catalog.</param>
    internal void ApplyCatalog(TreeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Catalog = catalog;
        _allItems = [.. catalog.Entries.Select(static entry => new TreeWorkspaceItem(entry))];
        if (_focusedEntry is not null && !_allItems.Any(item => item.Entry.Equals(_focusedEntry)))
        {
            _focusedEntry = null;
        }

        _focusedEntry ??= _allItems.FirstOrDefault()?.Entry;
        ApplyFilter();
    }

    /// <summary>
    /// Applies incremental matching across exact name, kind, mode, object, and size display.
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
    /// Moves controlled focus to one visible tree row.
    /// </summary>
    /// <param name="index">The absolute filtered row index.</param>
    internal void Focus(int index)
    {
        if (index < 0 || index >= VisibleItems.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        _focusedEntry = VisibleItems[index].Entry;
    }

    /// <summary>
    /// Replaces the object preview for one exact focused entry.
    /// </summary>
    /// <param name="entry">The exact entry whose object was captured.</param>
    /// <param name="text">The control-safe object presentation.</param>
    internal void SetPreview(TreeEntry entry, string text)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(text);
        PreviewTitle = $"{entry.Name.DisplayText} | {FormatKind(entry.Kind)} | {entry.ObjectId.ToString()[..12]}";
        Preview = CreatePreview(text);
    }

    /// <summary>
    /// Replaces the preview with an empty, loading, or failure message.
    /// </summary>
    /// <param name="text">The control-safe status message.</param>
    internal void SetPreviewMessage(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        PreviewTitle = "Object preview";
        Preview = CreatePreview(text);
    }

    private void ApplyFilter()
    {
        var filter = Filter.Text.Trim();
        VisibleItems = string.IsNullOrEmpty(filter)
            ? _allItems
            : [.. _allItems.Where(item => item.ToString().Contains(
                filter,
                StringComparison.CurrentCultureIgnoreCase) ||
                item.Entry.ObjectId.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase))];
        if (_focusedEntry is null || !VisibleItems.Any(item => item.Entry.Equals(_focusedEntry)))
        {
            _focusedEntry = VisibleItems.FirstOrDefault()?.Entry;
        }
    }

    private int GetFocusedIndex()
    {
        if (_focusedEntry is not null)
        {
            for (var index = 0; index < VisibleItems.Length; index++)
            {
                if (VisibleItems[index].Entry.Equals(_focusedEntry))
                {
                    return index;
                }
            }
        }

        return 0;
    }

    private static string FormatKind(TreeEntryKind kind)
        => kind switch
        {
            TreeEntryKind.Tree => "directory",
            TreeEntryKind.RegularFile => "file",
            TreeEntryKind.ExecutableFile => "executable",
            TreeEntryKind.SymbolicLink => "symbolic link",
            TreeEntryKind.GitLink => "submodule",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static EditorState CreatePreview(string text)
        => new(new Hex1bDocument(text))
        {
            IsReadOnly = true,
        };
}
