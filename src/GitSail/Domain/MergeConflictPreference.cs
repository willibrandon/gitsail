namespace GitSail.Domain;

/// <summary>
/// Selects an allowlisted ort conflict preference without discarding nonconflicting changes.
/// </summary>
internal enum MergeConflictPreference
{
    /// <summary>
    /// Leaves conflict resolution to Git's ordinary strategy behavior.
    /// </summary>
    Default,

    /// <summary>
    /// Auto-resolves conflicting hunks in favor of the current side.
    /// </summary>
    Ours,

    /// <summary>
    /// Auto-resolves conflicting hunks in favor of the incoming side.
    /// </summary>
    Theirs,
}
