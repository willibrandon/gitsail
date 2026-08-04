namespace GitSail.Domain;

/// <summary>
/// Identifies one action Git permits for an existing rebase transaction.
/// </summary>
internal enum RebaseControl
{
    /// <summary>
    /// Continues after the user resolves the current stop.
    /// </summary>
    Continue,

    /// <summary>
    /// Skips the commit currently being applied.
    /// </summary>
    Skip,

    /// <summary>
    /// Reopens the remaining interactive todo.
    /// </summary>
    EditTodo,

    /// <summary>
    /// Restores the state from before the rebase began.
    /// </summary>
    Abort,
}
