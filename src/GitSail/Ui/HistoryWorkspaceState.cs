using GitSail.Domain;
using GitSail.Localization.Generated;
using Hex1b.Documents;
using Hex1b.LanguageServer;
using Hex1b.Widgets;
using System.Collections.Immutable;

namespace GitSail.Ui;

/// <summary>
/// Owns structured history, controlled filtering and focus, and the lifted patch preview.
/// </summary>
internal sealed class HistoryWorkspaceState
{
    private ImmutableArray<HistoryWorkspaceItem> _allItems = [];
    private ObjectId? _focusedObjectId;

    /// <summary>
    /// Initializes empty history state with lifted filter and preview editors.
    /// </summary>
    internal HistoryWorkspaceState()
    {
        Filter = new TextBoxState();
        Preview = CreatePreview(AppMessages.HistoryPromptSelectCommit);
        PreviewDecorationProvider = new GitDiffDecorationProvider();
    }

    /// <summary>
    /// Gets the current structured history catalog.
    /// </summary>
    internal HistoryCatalog? Catalog { get; private set; }

    /// <summary>
    /// Gets the lifted incremental history-filter input.
    /// </summary>
    internal TextBoxState Filter { get; }

    /// <summary>
    /// Gets the history rows matching the current control-safe filter.
    /// </summary>
    internal ImmutableArray<HistoryWorkspaceItem> VisibleItems { get; private set; } = [];

    /// <summary>
    /// Gets the controlled cursor index in the filtered history list.
    /// </summary>
    internal int FocusedIndex => GetFocusedIndex();

    /// <summary>
    /// Gets the exact commit currently driving details and preview.
    /// </summary>
    internal HistoryWorkspaceItem? FocusedItem
        => VisibleItems.IsEmpty ? null : VisibleItems[GetFocusedIndex()];

    /// <summary>
    /// Gets the lifted read-only commit patch editor.
    /// </summary>
    internal EditorState Preview { get; }

    /// <summary>
    /// Gets the decoration provider owned by the current commit preview.
    /// </summary>
    internal ITextDecorationProvider PreviewDecorationProvider { get; }

    /// <summary>
    /// Gets the control-safe title for the current commit preview.
    /// </summary>
    internal string PreviewTitle { get; private set; } = AppMessages.HistoryPreviewTitle;

    /// <summary>
    /// Replaces the catalog while retaining exact object focus when possible.
    /// </summary>
    /// <param name="catalog">The newly captured structured history.</param>
    internal void ApplyCatalog(HistoryCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Catalog = catalog;
        _allItems = HistoryGraphBuilder.Build(catalog.Commits);
        if (_focusedObjectId is not null &&
            !_allItems.Any(item => item.Commit.ObjectId.Equals(_focusedObjectId)))
        {
            _focusedObjectId = null;
        }

        _focusedObjectId ??= _allItems.FirstOrDefault()?.Commit.ObjectId;
        ApplyFilter();
    }

    /// <summary>
    /// Applies incremental matching across identity, author, subject, body, and refs.
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
    /// Moves controlled focus to one visible commit row.
    /// </summary>
    /// <param name="index">The absolute filtered row index.</param>
    internal void Focus(int index)
    {
        if (index < 0 || index >= VisibleItems.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        _focusedObjectId = VisibleItems[index].Commit.ObjectId;
    }

    /// <summary>
    /// Replaces the read-only preview for one exact selected commit.
    /// </summary>
    /// <param name="commit">The exact commit whose details were captured.</param>
    /// <param name="text">The control-safe commit and patch presentation.</param>
    internal void SetPreview(HistoryCommit commit, string text)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(text);
        PreviewTitle = AppMessages.HistoryPreviewCommitTitle(commit.ObjectId.ToString()[..12]);
        ReplacePreviewText(text);
    }

    /// <summary>
    /// Replaces the preview with an empty, loading, or failure message.
    /// </summary>
    /// <param name="text">The control-safe status message.</param>
    internal void SetPreviewMessage(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        PreviewTitle = AppMessages.HistoryPreviewTitle;
        ReplacePreviewText(text);
    }

    /// <summary>
    /// Clears stale catalog and preview data while retaining the user's filter.
    /// </summary>
    internal void Clear()
    {
        Catalog = null;
        _allItems = [];
        VisibleItems = [];
        _focusedObjectId = null;
        SetPreviewMessage(AppMessages.HistoryPreviewReload);
    }

    private void ApplyFilter()
    {
        var filter = Filter.Text.Trim();
        VisibleItems = string.IsNullOrEmpty(filter)
            ? _allItems
            : [.. _allItems.Where(item => MatchesFilter(item.Commit, filter))];
        if (_focusedObjectId is null ||
            !VisibleItems.Any(item => item.Commit.ObjectId.Equals(_focusedObjectId)))
        {
            _focusedObjectId = VisibleItems.FirstOrDefault()?.Commit.ObjectId;
        }
    }

    private int GetFocusedIndex()
    {
        if (_focusedObjectId is not null)
        {
            for (var index = 0; index < VisibleItems.Length; index++)
            {
                if (VisibleItems[index].Commit.ObjectId.Equals(_focusedObjectId))
                {
                    return index;
                }
            }
        }

        return 0;
    }

    private static bool MatchesFilter(HistoryCommit commit, string filter)
        => commit.ObjectId.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            Decode(commit.AuthorName.Span).Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
            Decode(commit.AuthorEmail.Span).Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            Decode(commit.Subject.Span).Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
            Decode(commit.Body.Span).Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
            Decode(commit.Decorations.Span).Contains(filter, StringComparison.CurrentCultureIgnoreCase);

    private static string Decode(ReadOnlySpan<byte> bytes)
        => bytes.IsEmpty ? string.Empty : GitPath.FromUnixBytes(bytes).DisplayText;

    private static EditorState CreatePreview(string text)
        => new(new Hex1bDocument(text))
        {
            IsReadOnly = true,
        };

    private void ReplacePreviewText(string text)
    {
        if (string.Equals(Preview.Document.GetText(), text, StringComparison.Ordinal))
        {
            return;
        }

        Preview.Document.Apply(new ReplaceOperation(
            new DocumentRange(DocumentOffset.Zero, new DocumentOffset(Preview.Document.Length)),
            text));
        Preview.ClampAllCursors();
    }
}
