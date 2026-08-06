using GitSail.Domain;
using Hex1b.Documents;
using Hex1b.Widgets;
using System.Collections.Immutable;

namespace GitSail.Ui;

/// <summary>
/// Owns exact blame results, decoded presentation lines, controlled focus, and commit context.
/// </summary>
internal sealed class BlameWorkspaceState
{
    private readonly TerminalMouseReportFilter _inputFilter = new();
    private ImmutableArray<BlameWorkspaceItem> _allItems = [];
    private int? _focusedLine;

    /// <summary>
    /// Initializes empty line-history state and lifted search and line-navigation inputs.
    /// </summary>
    internal BlameWorkspaceState()
    {
        Filter = new TextBoxState();
        GoToLine = new TextBoxState();
        Preview = CreatePreview("Select a line to inspect its commit and nearby file history.");
        PreviewDecorationProvider = new GitSailDiffDecorationProvider();
    }

    /// <summary>
    /// Gets the exact content and separate attribution catalog.
    /// </summary>
    internal BlameCatalog? Catalog { get; private set; }

    /// <summary>
    /// Gets the lifted incremental line-search input.
    /// </summary>
    internal TextBoxState Filter { get; }

    /// <summary>
    /// Gets the lifted one-based line-navigation input.
    /// </summary>
    internal TextBoxState GoToLine { get; }

    /// <summary>
    /// Gets the blame rows matching the current control-safe search.
    /// </summary>
    internal ImmutableArray<BlameWorkspaceItem> VisibleItems { get; private set; } = [];

    /// <summary>
    /// Gets the controlled cursor index in the filtered blame list.
    /// </summary>
    internal int FocusedIndex => GetFocusedIndex();

    /// <summary>
    /// Gets the exact attributed line currently driving details and commit context.
    /// </summary>
    internal BlameWorkspaceItem? FocusedItem
        => VisibleItems.IsEmpty ? null : VisibleItems[GetFocusedIndex()];

    /// <summary>
    /// Gets the lifted read-only commit and history-context editor.
    /// </summary>
    internal EditorState Preview { get; private set; }

    /// <summary>
    /// Gets the diff decoration provider owned by the current commit preview.
    /// </summary>
    internal ITextDecorationProvider PreviewDecorationProvider { get; private set; }

    /// <summary>
    /// Gets the control-safe title for the current commit preview.
    /// </summary>
    internal string PreviewTitle { get; private set; } = "Commit and file history";

    /// <summary>
    /// Gets the detected or configured display encoding label.
    /// </summary>
    internal string EncodingLabel { get; private set; } = "UTF-8";

    /// <summary>
    /// Gets the optional display-only decoding warning.
    /// </summary>
    internal string? EncodingWarning { get; private set; }

    /// <summary>
    /// Replaces the catalog and focuses the requested line when it is present.
    /// </summary>
    /// <param name="catalog">The newly captured exact blame catalog.</param>
    /// <param name="preferredLine">The optional one-based result line to focus.</param>
    internal void ApplyCatalog(BlameCatalog catalog, int? preferredLine)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Catalog = catalog;
        var presentation = FileContentPresentationDecoder.DecodeLines(
            catalog.Content.Span,
            catalog.EncodingName);
        EncodingLabel = presentation.EncodingName;
        EncodingWarning = presentation.Warning;
        _allItems = [.. catalog.Attributions.Select(attribution => new BlameWorkspaceItem(
            attribution,
            attribution.ResultLineNumber <= presentation.Lines.Length
                ? presentation.Lines[attribution.ResultLineNumber - 1]
                : "<content line unavailable>"))];
        if (preferredLine is not null && _allItems.Any(item => item.Attribution.ResultLineNumber == preferredLine))
        {
            _focusedLine = preferredLine;
        }
        else if (_focusedLine is null || !_allItems.Any(item => item.Attribution.ResultLineNumber == _focusedLine))
        {
            _focusedLine = _allItems.FirstOrDefault()?.Attribution.ResultLineNumber;
        }

        GoToLine.Text = _focusedLine?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

        ApplyFilter();
    }

    /// <summary>
    /// Applies incremental matching across content, identity, author, summary, path, and line number.
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
    /// Moves controlled focus to one visible blame row.
    /// </summary>
    /// <param name="index">The absolute filtered row index.</param>
    internal void Focus(int index)
    {
        if (index < 0 || index >= VisibleItems.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        _focusedLine = VisibleItems[index].Attribution.ResultLineNumber;
        GoToLine.Text = _focusedLine.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Clears the search and focuses one exact loaded result line.
    /// </summary>
    /// <param name="lineNumber">The one-based result line to focus.</param>
    /// <returns><see langword="true"/> when the requested line exists in the loaded range.</returns>
    internal bool GoTo(int lineNumber)
    {
        var match = _allItems.FirstOrDefault(item => item.Attribution.ResultLineNumber == lineNumber);
        if (match is null)
        {
            return false;
        }

        Filter.Text = string.Empty;
        _focusedLine = lineNumber;
        GoToLine.Text = lineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ApplyFilter();
        return true;
    }

    /// <summary>
    /// Replaces the read-only context for one exact selected commit.
    /// </summary>
    /// <param name="attribution">The exact line attribution whose context was captured.</param>
    /// <param name="text">The terminal-safe history and patch presentation.</param>
    internal void SetPreview(BlameAttribution attribution, string text)
    {
        ArgumentNullException.ThrowIfNull(attribution);
        ArgumentNullException.ThrowIfNull(text);
        PreviewTitle = attribution.Commit.IsUncommitted
            ? "Uncommitted worktree line"
            : $"Commit {attribution.Commit.ObjectId.ToString()[..12]} | {attribution.SourcePath.DisplayText}:{attribution.SourceLineNumber}";
        Preview = CreatePreview(text);
        PreviewDecorationProvider = new GitSailDiffDecorationProvider();
    }

    /// <summary>
    /// Replaces the preview with an empty, loading, or failure message.
    /// </summary>
    /// <param name="text">The terminal-safe status message.</param>
    internal void SetPreviewMessage(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        PreviewTitle = "Commit and file history";
        Preview = CreatePreview(text);
        PreviewDecorationProvider = new GitSailDiffDecorationProvider();
    }

    /// <summary>
    /// Clears stale catalog and preview data while retaining the user's search.
    /// </summary>
    internal void Clear()
    {
        Catalog = null;
        _allItems = [];
        VisibleItems = [];
        _focusedLine = null;
        SetPreviewMessage("Reload blame to inspect line history.");
    }

    private void ApplyFilter()
    {
        var filter = Filter.Text.Trim();
        VisibleItems = string.IsNullOrEmpty(filter)
            ? _allItems
            : [.. _allItems.Where(item => MatchesFilter(item, filter))];
        if (_focusedLine is null || !VisibleItems.Any(item => item.Attribution.ResultLineNumber == _focusedLine))
        {
            _focusedLine = VisibleItems.FirstOrDefault()?.Attribution.ResultLineNumber;
        }
    }

    private int GetFocusedIndex()
    {
        if (_focusedLine is not null)
        {
            for (var index = 0; index < VisibleItems.Length; index++)
            {
                if (VisibleItems[index].Attribution.ResultLineNumber == _focusedLine)
                {
                    return index;
                }
            }
        }

        return 0;
    }

    private static bool MatchesFilter(BlameWorkspaceItem item, string filter)
    {
        var attribution = item.Attribution;
        return item.Content.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
            attribution.ResultLineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture).Contains(filter, StringComparison.Ordinal) ||
            attribution.Commit.ObjectId.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            attribution.SourcePath.DisplayText.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
            Decode(attribution.Commit.AuthorName.Span).Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
            Decode(attribution.Commit.AuthorEmail.Span).Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            Decode(attribution.Commit.Summary.Span).Contains(filter, StringComparison.CurrentCultureIgnoreCase);
    }

    private static string Decode(ReadOnlySpan<byte> bytes)
        => bytes.IsEmpty ? string.Empty : GitPath.FromUnixBytes(bytes).DisplayText;

    private static EditorState CreatePreview(string text)
        => new(new Hex1bDocument(text))
        {
            IsReadOnly = true,
        };
}
