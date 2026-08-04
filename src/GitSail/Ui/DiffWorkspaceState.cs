using GitSail.Domain;
using Hex1b.Documents;
using Hex1b.LanguageServer;
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

    /// <summary>
    /// Initializes empty comparison state with lifted filter and editor documents.
    /// </summary>
    internal DiffWorkspaceState()
    {
        Filter = new TextBoxState();
        UnifiedEditor = CreateEditor("Load a comparison to inspect changed files.");
        LeftEditor = CreateEditor("Load a comparison to inspect its left side.");
        RightEditor = CreateEditor("Load a comparison to inspect its right side.");
        UnifiedDecorationProvider = new GitDiffDecorationProvider();
        LeftDecorationProvider = new GitDiffDecorationProvider();
        RightDecorationProvider = new GitDiffDecorationProvider();
    }

    /// <summary>
    /// Gets the lifted incremental path-filter input.
    /// </summary>
    internal TextBoxState Filter { get; }

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
    /// Gets whether the aligned two-pane layout is selected.
    /// </summary>
    internal bool IsSideBySide { get; private set; } = true;

    /// <summary>
    /// Gets the control-safe unified editor title.
    /// </summary>
    internal string UnifiedTitle { get; private set; } = "Unified comparison";

    /// <summary>
    /// Gets the control-safe left editor title.
    /// </summary>
    internal string LeftTitle { get; private set; } = "Left";

    /// <summary>
    /// Gets the control-safe right editor title.
    /// </summary>
    internal string RightTitle { get; private set; } = "Right";

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
        UnifiedEditor = CreateEditor(presentation.UnifiedText);
        LeftEditor = CreateEditor(presentation.LeftText);
        RightEditor = CreateEditor(presentation.RightText);
        UnifiedDecorationProvider = new GitDiffDecorationProvider();
        LeftDecorationProvider = new GitDiffDecorationProvider();
        RightDecorationProvider = new GitDiffDecorationProvider();
        var path = TerminalTextSanitizer.Sanitize(file.NewPath.DisplayText);
        UnifiedTitle = $"Unified: {path}";
        LeftTitle = $"{leftLabel}: {TerminalTextSanitizer.Sanitize(file.OldPath.DisplayText)}";
        RightTitle = $"{rightLabel}: {path}";
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
        UnifiedEditor = CreateEditor(message);
        LeftEditor = CreateEditor(message);
        RightEditor = CreateEditor(message);
        UnifiedDecorationProvider = new GitDiffDecorationProvider();
        LeftDecorationProvider = new GitDiffDecorationProvider();
        RightDecorationProvider = new GitDiffDecorationProvider();
        UnifiedTitle = "Unified comparison";
        LeftTitle = "Left";
        RightTitle = "Right";
    }

    /// <summary>
    /// Switches between aligned two-pane and unified layouts without recapturing Git data.
    /// </summary>
    internal void ToggleLayout()
        => IsSideBySide = !IsSideBySide;

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
    /// Clears stale file and editor data while retaining the current filter and layout.
    /// </summary>
    internal void Clear()
    {
        _allItems = [];
        VisibleItems = [];
        _focusedPath = null;
        SetMessage("Reload the comparison to inspect changed files.");
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

    private static EditorState CreateEditor(string text)
        => new(new Hex1bDocument(text))
        {
            IsReadOnly = true,
        };
}
