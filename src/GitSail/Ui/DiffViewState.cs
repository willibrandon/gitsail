using GitSail.Domain;
using GitSail.Localization.Generated;
using Hex1b.Documents;
using Hex1b.LanguageServer;
using Hex1b.Widgets;
using System.Globalization;

namespace GitSail.Ui;

/// <summary>
/// Owns the current generation-stamped read-only editor presentation for the focused patch.
/// </summary>
internal sealed class DiffViewState
{
    private int _searchOffset = -1;
    private string _searchText = string.Empty;
    private int _tabSize = GitDiffRuntimeConfiguration.Default.TabSize;

    /// <summary>
    /// Initializes a safe empty diff presentation before a repository patch is selected.
    /// </summary>
    internal DiffViewState()
    {
        Editor = CreateEditor(AppMessages.WorkspacePromptSelectChangedPath);
        Search = new TextBoxState();
        DecorationProvider = new GitDiffDecorationProvider();
        Title = AppMessages.WorkspaceSectionDiff;
    }

    /// <summary>
    /// Gets the persistent case-insensitive diff-text search input.
    /// </summary>
    internal TextBoxState Search { get; }

    /// <summary>
    /// Gets the current one-based match position and total, or an empty value before searching.
    /// </summary>
    internal string SearchStatus { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the read-only editor state rendered by the workspace.
    /// </summary>
    internal EditorState Editor { get; private set; }

    /// <summary>
    /// Gets the document-owned diff decoration provider, or no provider for an editable merge result.
    /// </summary>
    internal ITextDecorationProvider? DecorationProvider { get; private set; }

    /// <summary>
    /// Gets the control-safe title describing the currently presented repository side and path.
    /// </summary>
    internal string Title { get; private set; }

    /// <summary>
    /// Gets the status generation that produced this presentation.
    /// </summary>
    internal OperationGeneration Generation { get; private set; }

    /// <summary>
    /// Applies the validated configured tab width to the current and every replacement diff editor.
    /// </summary>
    /// <param name="tabSize">The terminal-cell width from one through ninety-nine.</param>
    internal void SetTabSize(int tabSize)
    {
        if (tabSize is < 1 or > 99)
        {
            throw new ArgumentOutOfRangeException(nameof(tabSize));
        }

        _tabSize = tabSize;
        Editor.TabSize = tabSize;
    }

    /// <summary>
    /// Replaces the editor presentation with immutable generation-stamped content.
    /// </summary>
    /// <param name="title">The control-safe pane title.</param>
    /// <param name="text">The terminal-safe presentation text.</param>
    /// <param name="generation">The repository generation represented by the text.</param>
    /// <param name="preserveCursor">Whether to retain every cursor's logical line and column in the replacement document.</param>
    internal void SetContent(
        string title,
        string text,
        OperationGeneration generation,
        bool preserveCursor = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(text);
        var cursorPositions = preserveCursor
            ? Editor.Cursors
                .Select(cursor => (
                    Position: Editor.Document.OffsetToPosition(cursor.Position),
                    SelectionAnchor: cursor.SelectionAnchor is { } anchor
                        ? Editor.Document.OffsetToPosition(anchor)
                        : (DocumentPosition?)null))
                .ToArray()
            : [];
        var primaryCursorIndex = Editor.Cursors.PrimaryIndex;
        var replacementEditor = CreateEditor(text);
        if (cursorPositions.Length > 0)
        {
            replacementEditor.Cursors.Restore(new CursorSetSnapshot(
                [.. cursorPositions.Select(position => new CursorSnapshotEntry(
                    GetClampedOffset(replacementEditor.Document, position.Position),
                    position.SelectionAnchor is { } anchor
                        ? GetClampedOffset(replacementEditor.Document, anchor)
                        : null))],
                primaryCursorIndex));
        }

        Title = title;
        Generation = generation;
        _searchOffset = -1;
        SearchStatus = string.Empty;
        DecorationProvider = new GitDiffDecorationProvider();
        Editor = replacementEditor;
    }

    /// <summary>
    /// Replaces the case-insensitive diff-text query and resets match traversal when it changes.
    /// </summary>
    /// <param name="search">The current diff-text query.</param>
    internal void SetSearch(string search)
    {
        ArgumentNullException.ThrowIfNull(search);
        if (string.Equals(_searchText, search, StringComparison.Ordinal))
        {
            return;
        }

        _searchText = search;
        Search.Text = search;
        _searchOffset = -1;
        SearchStatus = string.Empty;
    }

    /// <summary>
    /// Selects the next or previous case-insensitive match with wraparound.
    /// </summary>
    /// <param name="reverse">Whether to traverse toward the start of the diff.</param>
    /// <returns><see langword="true"/> when the query matched the current diff.</returns>
    internal bool Find(bool reverse)
    {
        if (Search.Text.Length == 0)
        {
            _searchOffset = -1;
            SearchStatus = string.Empty;
            return false;
        }

        var text = Editor.Document.GetText();
        var matches = FindMatches(text, Search.Text);
        if (matches.Count == 0)
        {
            _searchOffset = -1;
            SearchStatus = "0/0";
            return false;
        }

        var cursorOffset = Editor.Cursor.SelectionStart.Value;
        var matchIndex = reverse
            ? FindPreviousMatchIndex(matches, _searchOffset >= 0 ? _searchOffset : cursorOffset)
            : FindNextMatchIndex(matches, _searchOffset >= 0 ? _searchOffset : cursorOffset - 1);
        _searchOffset = matches[matchIndex];
        Editor.SetCursorPosition(new DocumentOffset(_searchOffset));
        Editor.SetCursorPosition(
            new DocumentOffset(_searchOffset + Search.Text.Length),
            extend: true);
        SearchStatus = string.Create(
            CultureInfo.InvariantCulture,
            $"{matchIndex + 1}/{matches.Count}");
        return true;
    }

    /// <summary>
    /// Presents one lifted editor state without replacing its result buffer or undo history.
    /// </summary>
    /// <param name="title">The control-safe pane title.</param>
    /// <param name="editor">The lifted editor state to render and preserve across reconciliation.</param>
    /// <param name="generation">The repository generation represented by the editor.</param>
    internal void SetEditor(string title, EditorState editor, OperationGeneration generation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(editor);
        Title = title;
        Generation = generation;
        editor.TabSize = _tabSize;
        Editor = editor;
        DecorationProvider = null;
    }

    private EditorState CreateEditor(string text)
        => new(new Hex1bDocument(text))
        {
            IsReadOnly = true,
            TabSize = _tabSize,
        };

    private static DocumentOffset GetClampedOffset(
        IHex1bDocument document,
        DocumentPosition position)
    {
        var line = Math.Min(position.Line, document.LineCount);
        var column = Math.Min(position.Column, document.GetLineLength(line) + 1);
        return document.PositionToOffset(new DocumentPosition(line, column));
    }

    private static List<int> FindMatches(string text, string search)
    {
        var matches = new List<int>();
        var offset = 0;
        while (offset <= text.Length - search.Length)
        {
            var match = text.IndexOf(search, offset, StringComparison.OrdinalIgnoreCase);
            if (match < 0)
            {
                break;
            }

            matches.Add(match);
            offset = match + search.Length;
        }

        return matches;
    }

    private static int FindNextMatchIndex(List<int> matches, int currentOffset)
    {
        for (var index = 0; index < matches.Count; index++)
        {
            if (matches[index] > currentOffset)
            {
                return index;
            }
        }

        return 0;
    }

    private static int FindPreviousMatchIndex(List<int> matches, int currentOffset)
    {
        for (var index = matches.Count - 1; index >= 0; index--)
        {
            if (matches[index] < currentOffset)
            {
                return index;
            }
        }

        return matches.Count - 1;
    }
}
