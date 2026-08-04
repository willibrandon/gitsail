using GitSail.Domain;
using Hex1b.Documents;
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
        Title = "Diff";
    }

    /// <summary>
    /// Gets the read-only editor state rendered by the workspace.
    /// </summary>
    internal EditorState Editor { get; private set; }

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
    internal void SetContent(string title, string text, OperationGeneration generation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(text);
        Title = title;
        Generation = generation;
        Editor = CreateEditor(text);
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
    }

    private static EditorState CreateEditor(string text)
        => new(new Hex1bDocument(text))
        {
            IsReadOnly = true,
        };
}
