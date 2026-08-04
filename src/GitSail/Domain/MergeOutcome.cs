namespace GitSail.Domain;

/// <summary>
/// Classifies a Git-owned merge transition after exact execution.
/// </summary>
internal enum MergeOutcome
{
    /// <summary>
    /// Indicates that Git completed the merge and left no pending merge state.
    /// </summary>
    Completed,

    /// <summary>
    /// Indicates that Git stopped before creating the merge commit as requested.
    /// </summary>
    StoppedBeforeCommit,

    /// <summary>
    /// Indicates that Git prepared a squash result without merge ancestry.
    /// </summary>
    SquashPrepared,

    /// <summary>
    /// Indicates that Git left unmerged index entries for conflict resolution.
    /// </summary>
    Conflicts,
}
