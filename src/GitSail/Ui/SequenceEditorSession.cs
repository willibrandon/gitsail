using GitSail.Domain;

namespace GitSail.Ui;

/// <summary>
/// Owns focus, typed edits, validation, and completion state for one rebase todo.
/// </summary>
internal sealed class SequenceEditorSession
{
    /// <summary>
    /// Initializes an editor session over one authenticated todo document.
    /// </summary>
    /// <param name="document">The parsed Git-owned todo document.</param>
    internal SequenceEditorSession(RebaseTodoDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Document = document;
        FocusedIndex = FindFirstCommand(document);
        Status = "Review the complete plan, then save it to let Git begin the rebase.";
    }

    /// <summary>
    /// Notifies the view that controlled editor state changed.
    /// </summary>
    internal event Action? Changed;

    /// <summary>
    /// Gets the parsed todo document being edited.
    /// </summary>
    internal RebaseTodoDocument Document { get; }

    /// <summary>
    /// Gets the absolute focused line index.
    /// </summary>
    internal int FocusedIndex { get; private set; }

    /// <summary>
    /// Gets the currently focused todo line when one exists.
    /// </summary>
    internal RebaseTodoEntry? FocusedEntry
        => Document.Entries.IsEmpty ? null : Document.Entries[FocusedIndex];

    /// <summary>
    /// Gets the latest validation or editing status.
    /// </summary>
    internal string Status { get; private set; }

    /// <summary>
    /// Gets whether the user explicitly trusted every visible exec command.
    /// </summary>
    internal bool ExecCommandsTrusted { get; private set; }

    /// <summary>
    /// Gets whether this editor completed with a validated saved plan.
    /// </summary>
    internal bool IsSaved { get; private set; }

    /// <summary>
    /// Gets whether the document currently contains a shell-executing todo command.
    /// </summary>
    internal bool HasExecCommands
        => Document.Entries.Any(static entry => entry.Action == RebaseTodoAction.Exec);

    /// <summary>
    /// Focuses one exact todo line selected by keyboard or pointer.
    /// </summary>
    /// <param name="index">The absolute line index.</param>
    internal void Focus(int index)
    {
        if (index < 0 || index >= Document.Entries.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        FocusedIndex = index;
        NotifyChanged();
    }

    /// <summary>
    /// Moves line focus by a bounded relative offset without wrapping.
    /// </summary>
    /// <param name="offset">The signed line offset.</param>
    internal void MoveFocus(int offset)
    {
        if (Document.Entries.IsEmpty)
        {
            return;
        }

        FocusedIndex = Math.Clamp(FocusedIndex + offset, 0, Document.Entries.Length - 1);
        NotifyChanged();
    }

    /// <summary>
    /// Moves the focused command across its nearest command neighbor without wrapping.
    /// </summary>
    /// <param name="offset">The requested negative or positive direction.</param>
    internal void MoveCommand(int offset)
    {
        if (FocusedEntry?.Kind != RebaseTodoLineKind.Command)
        {
            Status = "Select a todo command before moving it.";
            NotifyChanged();
            return;
        }

        var previous = FocusedIndex;
        FocusedIndex = Document.MoveCommand(FocusedIndex, offset);
        Status = previous == FocusedIndex
            ? "The selected command is already at that boundary."
            : "Moved the selected command.";
        NotifyChanged();
    }

    /// <summary>
    /// Changes the focused commit row to one typed sequencer action.
    /// </summary>
    /// <param name="action">The replacement commit action.</param>
    internal void ChangeAction(RebaseTodoAction action)
    {
        var entry = FocusedEntry;
        if (entry?.Action is not (RebaseTodoAction.Pick or
            RebaseTodoAction.Reword or
            RebaseTodoAction.Edit or
            RebaseTodoAction.Squash or
            RebaseTodoAction.Fixup or
            RebaseTodoAction.Drop))
        {
            Status = "Select a commit todo line before changing its action.";
            NotifyChanged();
            return;
        }

        var previousAction = entry.Action.Value;
        try
        {
            entry.ChangeCommitAction(action);
            RebaseTodoValidator.Validate(Document);
            Status = $"Changed the selected commit to {RebaseTodoParser.GetCommandName(action)}.";
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
        {
            entry.ChangeCommitAction(previousAction);
            Status = exception.Message;
        }

        NotifyChanged();
    }

    /// <summary>
    /// Inserts one explicitly trusted exec command after the focused line.
    /// </summary>
    /// <param name="command">The exact one-line shell command entered by the user.</param>
    internal void InsertTrustedExec(string command)
    {
        try
        {
            FocusedIndex = Document.InsertExec(
                Document.Entries.IsEmpty ? -1 : FocusedIndex,
                command);
            ExecCommandsTrusted = true;
            Status = "Added the trusted exec command. Git will run it through a shell during rebase.";
        }
        catch (ArgumentException exception)
        {
            Status = exception.Message;
        }

        NotifyChanged();
    }

    /// <summary>
    /// Removes the focused exec command from the plan.
    /// </summary>
    internal void RemoveExec()
    {
        if (FocusedEntry?.Action != RebaseTodoAction.Exec)
        {
            Status = "Select an exec command before removing it.";
            NotifyChanged();
            return;
        }

        FocusedIndex = Document.RemoveExec(FocusedIndex);
        ExecCommandsTrusted = !HasExecCommands;
        Status = "Removed the selected exec command.";
        NotifyChanged();
    }

    /// <summary>
    /// Records explicit trust for all existing shell-executing todo commands.
    /// </summary>
    internal void TrustExecCommands()
    {
        ExecCommandsTrusted = true;
        Status = "Exec commands trusted for this rebase plan.";
        NotifyChanged();
    }

    /// <summary>
    /// Validates and completes this editor with a saved plan.
    /// </summary>
    /// <returns><see langword="true"/> when the plan can be returned to Git.</returns>
    internal bool TrySave()
    {
        try
        {
            RebaseTodoValidator.Validate(Document);
            if (HasExecCommands && !ExecCommandsTrusted)
            {
                Status = "Review and explicitly trust the exec commands before saving this plan.";
                NotifyChanged();
                return false;
            }

            IsSaved = true;
            Status = "Plan saved.";
            NotifyChanged();
            return true;
        }
        catch (InvalidDataException exception)
        {
            Status = exception.Message;
            NotifyChanged();
            return false;
        }
    }

    private static int FindFirstCommand(RebaseTodoDocument document)
    {
        for (var index = 0; index < document.Entries.Length; index++)
        {
            if (document.Entries[index].Kind == RebaseTodoLineKind.Command)
            {
                return index;
            }
        }

        return 0;
    }

    private void NotifyChanged()
        => Changed?.Invoke();
}
