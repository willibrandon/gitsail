using GitSail.Domain;
using Hex1b.Documents;
using Hex1b.Widgets;

namespace GitSail.Ui;

/// <summary>
/// Owns the lifted writable editor state for a recoverable commit-message draft.
/// </summary>
internal sealed class CommitMessageState
{
    private string _initialMessage;
    private CommitMessageInitializationKind _initializationKind;

    /// <summary>
    /// Initializes the editor from the selected message and retains template-origin safeguards.
    /// </summary>
    /// <param name="message">The initial decoded commit message.</param>
    /// <param name="initializationKind">The source that supplied the initial message.</param>
    /// <param name="spelling">The optional controlled spelling state for this editor.</param>
    internal CommitMessageState(
        string message = "",
        CommitMessageInitializationKind initializationKind = CommitMessageInitializationKind.Empty,
        SpellingState? spelling = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!Enum.IsDefined(initializationKind))
        {
            throw new ArgumentOutOfRangeException(nameof(initializationKind));
        }

        _initialMessage = message;
        _initializationKind = initializationKind;
        Spelling = spelling ?? new SpellingState();
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
    /// Gets the live optional spelling result associated with this editor document.
    /// </summary>
    internal SpellingState Spelling { get; }

    /// <summary>
    /// Gets the current complete editor message.
    /// </summary>
    internal string Message => Editor.Document.GetText();

    /// <summary>
    /// Gets the monotonic document version for commit-success reconciliation.
    /// </summary>
    internal long Version => Editor.Document.Version;

    /// <summary>
    /// Gets whether the configured template remains exactly unchanged and must be edited before commit.
    /// </summary>
    internal bool IsInitialTemplateUnchanged =>
        _initializationKind == CommitMessageInitializationKind.Template &&
        string.Equals(Message, _initialMessage, StringComparison.Ordinal);

    /// <summary>
    /// Applies a newly pending Git merge or squash message only when the current draft remains untouched.
    /// </summary>
    /// <param name="initialization">The recovery-precedence operation message selected from repository state.</param>
    /// <returns><see langword="true"/> when the editor adopted the pending operation message.</returns>
    internal bool TryApplyPendingOperationMessage(CommitMessageInitialization initialization)
    {
        ArgumentNullException.ThrowIfNull(initialization);
        if (initialization.Kind is not (
                CommitMessageInitializationKind.Recovery or
                CommitMessageInitializationKind.Merge or
                CommitMessageInitializationKind.Squash))
        {
            throw new ArgumentException(
                "A pending operation message must come from recovery, merge, or squash state.",
                nameof(initialization));
        }

        if (_initializationKind == CommitMessageInitializationKind.Recovery ||
            initialization.Kind == CommitMessageInitializationKind.Recovery ||
            !string.Equals(Message, _initialMessage, StringComparison.Ordinal))
        {
            return false;
        }

        Editor.Document.Changed -= HandleDocumentChanged;
        _initialMessage = initialization.Message;
        _initializationKind = initialization.Kind;
        Editor = CreateEditor(initialization.Message);
        Editor.Document.Changed += HandleDocumentChanged;
        Spelling.Clear(Editor.Document.Version);
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Replaces a successfully committed draft with a new empty editor state.
    /// </summary>
    internal void Clear()
    {
        Editor.Document.Changed -= HandleDocumentChanged;
        _initialMessage = string.Empty;
        _initializationKind = CommitMessageInitializationKind.Empty;
        Editor = CreateEditor(string.Empty);
        Editor.Document.Changed += HandleDocumentChanged;
        Spelling.Clear(Editor.Document.Version);
    }

    /// <summary>
    /// Replaces one still-current misspelling with an exact selected suggestion.
    /// </summary>
    /// <param name="issue">The version-matched issue selected by the user.</param>
    /// <param name="replacement">The exact bounded checker suggestion to insert.</param>
    /// <returns><see langword="true"/> when the expected misspelled text was replaced.</returns>
    internal bool TryReplaceSpellingIssue(SpellingIssue issue, string replacement)
    {
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentNullException.ThrowIfNull(replacement);
        if (!issue.Suggestions.Contains(replacement, StringComparer.Ordinal) ||
            issue.Offset < 0 || issue.Length <= 0 ||
            issue.Offset + issue.Length > Editor.Document.Length)
        {
            return false;
        }

        var range = new DocumentRange(
            new DocumentOffset(issue.Offset),
            new DocumentOffset(issue.Offset + issue.Length));
        if (!string.Equals(Editor.Document.GetText(range), issue.Word, StringComparison.Ordinal))
        {
            return false;
        }

        Editor.Document.Apply(new ReplaceOperation(range, replacement), "Replace misspelling");
        Editor.SetCursorPosition(new DocumentOffset(issue.Offset + replacement.Length));
        return true;
    }

    private void HandleDocumentChanged(object? sender, DocumentChangedEventArgs eventArgs)
    {
        Spelling.Clear(Editor.Document.Version);
        Changed?.Invoke();
    }

    private static EditorState CreateEditor(string message)
        => new(new Hex1bDocument(message));
}
