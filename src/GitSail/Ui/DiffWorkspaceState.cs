using GitSail.Domain;
using GitSail.Localization.Generated;
using Hex1b.Documents;
using Hex1b.Widgets;
using System.Collections.Immutable;

namespace GitSail.Ui;

/// <summary>
/// Owns comparison files, filtering, focus, and lifted unified and aligned editor states.
/// </summary>
internal sealed class DiffWorkspaceState
{
    private ImmutableArray<DiffWorkspaceItem> _allItems = [];
    private GitPath? _focusedPath;
    private ComparisonPresentation? _presentation;
    private int _focusedHunkIndex;
    private EditorState? _searchEditor;
    private string? _searchText;
    private int _searchOffset = -1;
    private readonly int _tabSize;

    /// <summary>
    /// Initializes empty comparison state with lifted filter and editor documents.
    /// </summary>
    /// <param name="tabSize">The validated terminal-cell width used to present tab characters.</param>
    internal DiffWorkspaceState(int tabSize = 8)
    {
        if (tabSize is < 1 or > 99)
        {
            throw new ArgumentOutOfRangeException(nameof(tabSize));
        }

        _tabSize = tabSize;
        Filter = new TextBoxState();
        Search = new TextBoxState();
        GoToLine = new TextBoxState();
        UnifiedEditor = CreateEditor(AppMessages.DiffPromptLoadComparisonFiles);
        LeftEditor = CreateEditor(AppMessages.DiffPromptLoadComparisonLeft);
        RightEditor = CreateEditor(AppMessages.DiffPromptLoadComparisonRight);
        UnifiedDecorationProvider = CreateDecorationProvider([]);
        LeftDecorationProvider = CreateDecorationProvider([]);
        RightDecorationProvider = CreateDecorationProvider([]);
        UnifiedGutterProvider = CreateGutterProvider([], showOld: true, showNew: true);
        LeftGutterProvider = CreateGutterProvider([], showOld: true, showNew: false);
        RightGutterProvider = CreateGutterProvider([], showOld: false, showNew: true);
    }

    /// <summary>
    /// Gets the lifted incremental path-filter input.
    /// </summary>
    internal TextBoxState Filter { get; }

    /// <summary>
    /// Gets the lifted text searched within the active comparison layout.
    /// </summary>
    internal TextBoxState Search { get; }

    /// <summary>
    /// Gets the lifted one-based presentation-line input.
    /// </summary>
    internal TextBoxState GoToLine { get; }

    /// <summary>
    /// Gets the changed-file rows matching the current path filter.
    /// </summary>
    internal ImmutableArray<DiffWorkspaceItem> VisibleItems { get; private set; } = [];

    /// <summary>
    /// Gets the controlled cursor index in the filtered changed-file list.
    /// </summary>
    internal int FocusedIndex => GetFocusedIndex();

    /// <summary>
    /// Gets the exact file patch currently driving the comparison editors.
    /// </summary>
    internal DiffWorkspaceItem? FocusedItem
        => VisibleItems.IsEmpty ? null : VisibleItems[GetFocusedIndex()];

    /// <summary>
    /// Gets the lifted read-only unified patch editor.
    /// </summary>
    internal EditorState UnifiedEditor { get; private set; }

    /// <summary>
    /// Gets the lifted read-only aligned left-side editor.
    /// </summary>
    internal EditorState LeftEditor { get; private set; }

    /// <summary>
    /// Gets the lifted read-only aligned right-side editor.
    /// </summary>
    internal EditorState RightEditor { get; private set; }

    /// <summary>
    /// Gets the unified Git diff decoration provider.
    /// </summary>
    internal ITextDecorationProvider UnifiedDecorationProvider { get; private set; }

    /// <summary>
    /// Gets the left-side Git diff decoration provider.
    /// </summary>
    internal ITextDecorationProvider LeftDecorationProvider { get; private set; }

    /// <summary>
    /// Gets the right-side Git diff decoration provider.
    /// </summary>
    internal ITextDecorationProvider RightDecorationProvider { get; private set; }

    /// <summary>
    /// Gets the dual old/new semantic line-number gutter for unified comparison rows.
    /// </summary>
    internal IGutterProvider UnifiedGutterProvider { get; private set; }

    /// <summary>
    /// Gets the old-side semantic line-number gutter for aligned comparison rows.
    /// </summary>
    internal IGutterProvider LeftGutterProvider { get; private set; }

    /// <summary>
    /// Gets the new-side semantic line-number gutter for aligned comparison rows.
    /// </summary>
    internal IGutterProvider RightGutterProvider { get; private set; }

    /// <summary>
    /// Gets whether the aligned two-pane layout is selected.
    /// </summary>
    internal bool IsSideBySide { get; private set; } = true;

    /// <summary>
    /// Gets the control-safe unified editor title.
    /// </summary>
    internal string UnifiedTitle { get; private set; } = AppMessages.DiffTitleUnified;

    /// <summary>
    /// Gets the control-safe left editor title.
    /// </summary>
    internal string LeftTitle { get; private set; } = AppMessages.DiffTitleLeft;

    /// <summary>
    /// Gets the control-safe right editor title.
    /// </summary>
    internal string RightTitle { get; private set; } = AppMessages.DiffTitleRight;

    /// <summary>
    /// Replaces the changed-file catalog while retaining exact path focus where possible.
    /// </summary>
    /// <param name="files">The newly captured immutable raw file index.</param>
    internal void ApplyFiles(ImmutableArray<RawDiffFile> files)
    {
        _allItems = [.. files.Select(static file => new DiffWorkspaceItem(file))];
        if (_focusedPath is not null &&
            !_allItems.Any(item => MatchesPath(item.File, _focusedPath)))
        {
            _focusedPath = null;
        }

        _focusedPath ??= _allItems.FirstOrDefault()?.File.NewPath;
        ApplyFilter();
    }

    /// <summary>
    /// Applies incremental matching across old and new path displays.
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
    /// Moves controlled focus to one visible changed-file row.
    /// </summary>
    /// <param name="index">The absolute filtered row index.</param>
    internal void Focus(int index)
    {
        if (index < 0 || index >= VisibleItems.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        _focusedPath = VisibleItems[index].File.NewPath;
    }

    /// <summary>
    /// Replaces all editor layouts with one generation-consistent file presentation.
    /// </summary>
    /// <param name="file">The exact selected raw file patch.</param>
    /// <param name="presentation">The derived unified and aligned presentation text.</param>
    /// <param name="leftLabel">The left repository-state label.</param>
    /// <param name="rightLabel">The right repository-state label.</param>
    internal void SetPresentation(
        RawDiffFile file,
        ComparisonPresentation presentation,
        string leftLabel,
        string rightLabel)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(presentation);
        ArgumentException.ThrowIfNullOrWhiteSpace(leftLabel);
        ArgumentException.ThrowIfNullOrWhiteSpace(rightLabel);
        _presentation = presentation;
        _focusedHunkIndex = 0;
        ResetSearchPosition();
        UnifiedEditor = CreateEditor(presentation.UnifiedText);
        LeftEditor = CreateEditor(presentation.LeftText);
        RightEditor = CreateEditor(presentation.RightText);
        UnifiedDecorationProvider = CreateDecorationProvider(presentation.UnifiedHighlights);
        LeftDecorationProvider = CreateDecorationProvider(presentation.LeftHighlights);
        RightDecorationProvider = CreateDecorationProvider(presentation.RightHighlights);
        UnifiedGutterProvider = CreateGutterProvider(
            presentation.UnifiedLineNumbers,
            showOld: true,
            showNew: true);
        LeftGutterProvider = CreateGutterProvider(
            presentation.SideLineNumbers,
            showOld: true,
            showNew: false);
        RightGutterProvider = CreateGutterProvider(
            presentation.SideLineNumbers,
            showOld: false,
            showNew: true);
        var path = TerminalTextSanitizer.Sanitize(file.NewPath.DisplayText);
        UnifiedTitle = AppMessages.DiffTitleUnifiedPath(path);
        LeftTitle = AppMessages.DiffTitleSidePath(
            path: TerminalTextSanitizer.Sanitize(file.OldPath.DisplayText),
            side: leftLabel);
        RightTitle = AppMessages.DiffTitleSidePath(path: path, side: rightLabel);
        FocusCurrentHunk();
    }

    /// <summary>
    /// Replaces all editors with a loading, empty, or actionable failure message.
    /// </summary>
    /// <param name="message">The control-safe status message.</param>
    internal void SetMessage(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _presentation = null;
        ResetSearchPosition();
        UnifiedEditor = CreateEditor(message);
        LeftEditor = CreateEditor(message);
        RightEditor = CreateEditor(message);
        UnifiedDecorationProvider = CreateDecorationProvider([]);
        LeftDecorationProvider = CreateDecorationProvider([]);
        RightDecorationProvider = CreateDecorationProvider([]);
        UnifiedGutterProvider = CreateGutterProvider([], showOld: true, showNew: true);
        LeftGutterProvider = CreateGutterProvider([], showOld: true, showNew: false);
        RightGutterProvider = CreateGutterProvider([], showOld: false, showNew: true);
        UnifiedTitle = AppMessages.DiffTitleUnified;
        LeftTitle = AppMessages.DiffTitleLeft;
        RightTitle = AppMessages.DiffTitleRight;
    }

    /// <summary>
    /// Switches between aligned two-pane and unified layouts without recapturing Git data.
    /// </summary>
    internal void ToggleLayout()
    {
        IsSideBySide = !IsSideBySide;
        ResetSearchPosition();
    }

    /// <summary>
    /// Moves the controlled hunk focus by one relative offset in the selected layout.
    /// </summary>
    /// <param name="offset">The signed hunk offset.</param>
    /// <returns><see langword="true"/> when a hunk was available and focused.</returns>
    internal bool MoveHunk(int offset)
    {
        if (_presentation is null || _presentation.UnifiedHunkLines.IsEmpty)
        {
            return false;
        }

        _focusedHunkIndex = Math.Clamp(
            _focusedHunkIndex + offset,
            0,
            _presentation.UnifiedHunkLines.Length - 1);
        FocusCurrentHunk();
        return true;
    }

    /// <summary>
    /// Selects the next or previous case-insensitive text match in the active layout.
    /// </summary>
    /// <param name="reverse">Whether to search toward the start of the presentation.</param>
    /// <returns><see langword="true"/> when a match was selected.</returns>
    internal bool FindText(bool reverse)
    {
        var search = Search.Text;
        if (string.IsNullOrEmpty(search))
        {
            ResetSearchPosition();
            return false;
        }

        var editors = IsSideBySide
            ? new[] { LeftEditor, RightEditor }
            : [UnifiedEditor];
        var searchChanged = !string.Equals(search, _searchText, StringComparison.Ordinal);
        var currentIndex = Array.IndexOf(editors, _searchEditor);
        if (!searchChanged && currentIndex >= 0 &&
            TryFindInEditor(
                editors[currentIndex],
                search,
                reverse,
                _searchOffset,
                continueCurrent: true,
                out var continuedOffset))
        {
            SelectMatch(editors[currentIndex], search, continuedOffset);
            return true;
        }

        var start = searchChanged || currentIndex < 0
            ? (reverse ? editors.Length - 1 : 0)
            : WrapIndex(currentIndex + (reverse ? -1 : 1), editors.Length);
        for (var count = 0; count < editors.Length; count++)
        {
            var index = WrapIndex(start + (reverse ? -count : count), editors.Length);
            if (!searchChanged && index == currentIndex)
            {
                continue;
            }

            if (TryFindInEditor(
                editors[index],
                search,
                reverse,
                offset: -1,
                continueCurrent: false,
                out var foundOffset))
            {
                SelectMatch(editors[index], search, foundOffset);
                return true;
            }
        }

        if (!searchChanged && currentIndex >= 0 &&
            TryFindInEditor(
                editors[currentIndex],
                search,
                reverse,
                offset: -1,
                continueCurrent: false,
                out var wrappedOffset))
        {
            SelectMatch(editors[currentIndex], search, wrappedOffset);
            return true;
        }

        _searchText = search;
        _searchEditor = null;
        _searchOffset = -1;
        return false;
    }

    /// <summary>
    /// Moves every active comparison editor to one one-based presentation line.
    /// </summary>
    /// <param name="lineNumber">The requested one-based presentation line.</param>
    /// <returns><see langword="true"/> when the line exists in the active layout.</returns>
    internal bool GoToPresentationLine(int lineNumber)
    {
        var editors = IsSideBySide
            ? new[] { LeftEditor, RightEditor }
            : [UnifiedEditor];
        if (lineNumber <= 0 || editors.Any(editor => lineNumber > editor.Document.LineCount))
        {
            return false;
        }

        foreach (var editor in editors)
        {
            SetCursorLine(editor, lineNumber);
        }

        ResetSearchPosition();
        return true;
    }

    /// <summary>
    /// Clears stale file and editor data while retaining the current filter and layout.
    /// </summary>
    internal void Clear()
    {
        _allItems = [];
        VisibleItems = [];
        _focusedPath = null;
        SetMessage(AppMessages.DiffPromptReloadComparisonFiles);
    }

    private void ApplyFilter()
    {
        var filter = Filter.Text.Trim();
        VisibleItems = string.IsNullOrEmpty(filter)
            ? _allItems
            : [.. _allItems.Where(item =>
                item.File.OldPath.DisplayText.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
                item.File.NewPath.DisplayText.Contains(filter, StringComparison.CurrentCultureIgnoreCase))];
        if (_focusedPath is null || !VisibleItems.Any(item => MatchesPath(item.File, _focusedPath)))
        {
            _focusedPath = VisibleItems.FirstOrDefault()?.File.NewPath;
        }
    }

    private void FocusCurrentHunk()
    {
        if (_presentation is null || _presentation.UnifiedHunkLines.IsEmpty)
        {
            return;
        }

        SetCursorLine(UnifiedEditor, _presentation.UnifiedHunkLines[_focusedHunkIndex]);
        SetCursorLine(LeftEditor, _presentation.SideHunkLines[_focusedHunkIndex]);
        SetCursorLine(RightEditor, _presentation.SideHunkLines[_focusedHunkIndex]);
    }

    private static bool TryFindInEditor(
        EditorState editor,
        string search,
        bool reverse,
        int offset,
        bool continueCurrent,
        out int foundOffset)
    {
        var text = editor.Document.GetText();
        if (text.Length == 0)
        {
            foundOffset = -1;
            return false;
        }

        if (reverse)
        {
            var start = continueCurrent
                ? Math.Min(offset - 1, text.Length - 1)
                : text.Length - 1;
            foundOffset = start < 0
                ? -1
                : text.LastIndexOf(search, start, StringComparison.OrdinalIgnoreCase);
            return foundOffset >= 0;
        }

        var forwardStart = continueCurrent ? Math.Max(0, offset + 1) : 0;
        foundOffset = forwardStart > text.Length - search.Length
            ? -1
            : text.IndexOf(search, forwardStart, StringComparison.OrdinalIgnoreCase);
        return foundOffset >= 0;
    }

    private void SelectMatch(EditorState editor, string search, int offset)
    {
        editor.SetCursorPosition(new DocumentOffset(offset));
        editor.SetCursorPosition(new DocumentOffset(offset + search.Length), extend: true);
        _searchEditor = editor;
        _searchText = search;
        _searchOffset = offset;
    }

    private void ResetSearchPosition()
    {
        _searchEditor = null;
        _searchText = null;
        _searchOffset = -1;
    }

    private static int WrapIndex(int index, int count)
        => ((index % count) + count) % count;

    private int GetFocusedIndex()
    {
        if (_focusedPath is not null)
        {
            for (var index = 0; index < VisibleItems.Length; index++)
            {
                if (MatchesPath(VisibleItems[index].File, _focusedPath))
                {
                    return index;
                }
            }
        }

        return 0;
    }

    private static bool MatchesPath(RawDiffFile file, GitPath path)
        => file.NewPath.Equals(path) || file.OldPath.Equals(path);

    private static void SetCursorLine(EditorState editor, int line)
    {
        var clampedLine = Math.Clamp(line, 1, editor.Document.LineCount);
        editor.SetCursorPosition(editor.Document.PositionToOffset(new DocumentPosition(clampedLine, 1)));
    }

    private static ComparisonDecorationProvider CreateDecorationProvider(
        ImmutableArray<ComparisonHighlight> highlights)
        => new ComparisonDecorationProvider(highlights);

    private static ComparisonLineNumberGutterProvider CreateGutterProvider(
        ImmutableArray<ComparisonLineNumber> lineNumbers,
        bool showOld,
        bool showNew)
        => new(lineNumbers, showOld, showNew);

    private EditorState CreateEditor(string text)
        => new(new Hex1bDocument(text))
        {
            IsReadOnly = true,
            TabSize = _tabSize,
        };
}
