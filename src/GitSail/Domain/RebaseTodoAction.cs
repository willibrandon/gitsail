namespace GitSail.Domain;

/// <summary>
/// Identifies one command understood by Git's interactive-rebase sequencer.
/// </summary>
internal enum RebaseTodoAction
{
    /// <summary>
    /// Applies a commit without editing its message.
    /// </summary>
    Pick,

    /// <summary>
    /// Applies a commit and opens its message for editing.
    /// </summary>
    Reword,

    /// <summary>
    /// Applies a commit and stops for amendment.
    /// </summary>
    Edit,

    /// <summary>
    /// Combines a commit with the preceding commit and edits the combined message.
    /// </summary>
    Squash,

    /// <summary>
    /// Combines a commit with the preceding commit and discards this commit's message.
    /// </summary>
    Fixup,

    /// <summary>
    /// Omits a commit from the rewritten history.
    /// </summary>
    Drop,

    /// <summary>
    /// Runs an explicitly entered shell command.
    /// </summary>
    Exec,

    /// <summary>
    /// Stops the sequencer before the next todo command.
    /// </summary>
    Break,

    /// <summary>
    /// Assigns a sequencer label to the current head.
    /// </summary>
    Label,

    /// <summary>
    /// Resets the sequencer head to a label.
    /// </summary>
    Reset,

    /// <summary>
    /// Recreates a merge commit while preserving a merge topology.
    /// </summary>
    Merge,

    /// <summary>
    /// Records a ref for update after the rebase completes.
    /// </summary>
    UpdateRef,

    /// <summary>
    /// Performs no sequencer operation.
    /// </summary>
    Noop,
}
