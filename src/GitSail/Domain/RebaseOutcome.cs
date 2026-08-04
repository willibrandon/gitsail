namespace GitSail.Domain;

/// <summary>
/// Identifies the repository outcome after a Git rebase command returns.
/// </summary>
internal enum RebaseOutcome
{
    /// <summary>
    /// Identifies a rebase that completed and removed its sequencer state.
    /// </summary>
    Completed,

    /// <summary>
    /// Identifies a rebase that stopped with recoverable Git-owned state.
    /// </summary>
    Stopped,
}
