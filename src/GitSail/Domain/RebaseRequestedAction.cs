namespace GitSail.Domain;

/// <summary>
/// Identifies the terminal-attached Git action requested after the rebase TUI stops.
/// </summary>
internal enum RebaseRequestedAction
{
    /// <summary>
    /// Starts the exact confirmed interactive-rebase plan.
    /// </summary>
    Start,

    /// <summary>
    /// Continues the exact displayed rebase state.
    /// </summary>
    Continue,

    /// <summary>
    /// Skips the exact displayed current rebase commit.
    /// </summary>
    Skip,

    /// <summary>
    /// Opens the remaining todo through the authenticated helper.
    /// </summary>
    EditTodo,

    /// <summary>
    /// Opens the repository workspace for conflict resolution and staging.
    /// </summary>
    OpenWorkspace,

    /// <summary>
    /// Aborts the exact displayed rebase transaction.
    /// </summary>
    Abort,
}
