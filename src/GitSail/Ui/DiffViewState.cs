using GitSail.Domain;
using Hex1b.Documents;
using Hex1b.LanguageServer;
using Hex1b.Widgets;

namespace GitSail.Ui;

/// <summary>
/// Owns the current generation-stamped read-only editor presentation for the focused patch.
/// </summary>
internal sealed class DiffViewState
{
    /// <summary>
    /// Initializes a safe empty diff presentation before a repository patch is selected.
    /// </summary>
    internal DiffViewState()
    {
        Editor = CreateEditor("Select a changed path to inspect its patch.");
        DecorationProvider = new GitDiffDecorationProvider();
        Title = "Diff";
    }

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
        DecorationProvider = new GitDiffDecorationProvider();
        Editor = replacementEditor;
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
        Editor = editor;
        DecorationProvider = null;
    }

    private static EditorState CreateEditor(string text)
        => new(new Hex1bDocument(text))
        {
            IsReadOnly = true,
        };

    private static DocumentOffset GetClampedOffset(
        IHex1bDocument document,
        DocumentPosition position)
    {
        var line = Math.Min(position.Line, document.LineCount);
        var column = Math.Min(position.Column, document.GetLineLength(line) + 1);
        return document.PositionToOffset(new DocumentPosition(line, column));
    }
}
