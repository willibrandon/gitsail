using Hex1b.Documents;
using Hex1b.Widgets;

namespace GitSail.Ui;

/// <summary>
/// Owns the lifted writable editor state for a recoverable commit-message draft.
/// </summary>
internal sealed class CommitMessageState
{
    /// <summary>
    /// Initializes the editor from an empty or recovered draft.
    /// </summary>
    /// <param name="message">The initial decoded commit message.</param>
    internal CommitMessageState(string message = "")
    {
        ArgumentNullException.ThrowIfNull(message);
        Editor = CreateEditor(message);
        Editor.Document.Changed += HandleDocumentChanged;
    }

    /// <summary>
    /// Notifies the owning repository session after an editor mutation changes the complete message.
    /// </summary>
    internal event Action? Changed;

    /// <summary>
    /// Gets the writable editor state preserved across view reconciliation.
    /// </summary>
    internal EditorState Editor { get; private set; }

    /// <summary>
    /// Gets the current complete editor message.
    /// </summary>
    internal string Message => Editor.Document.GetText();

    /// <summary>
    /// Gets the monotonic document version for commit-success reconciliation.
    /// </summary>
    internal long Version => Editor.Document.Version;

    /// <summary>
    /// Replaces a successfully committed draft with a new empty editor state.
    /// </summary>
    internal void Clear()
    {
        Editor.Document.Changed -= HandleDocumentChanged;
        Editor = CreateEditor(string.Empty);
        Editor.Document.Changed += HandleDocumentChanged;
    }

    private void HandleDocumentChanged(object? sender, DocumentChangedEventArgs eventArgs)
        => Changed?.Invoke();

    private static EditorState CreateEditor(string message)
        => new(new Hex1bDocument(message));
}
