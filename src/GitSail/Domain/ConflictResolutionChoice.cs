namespace GitSail.Domain;

/// <summary>
/// Identifies one exact content choice for a three-way conflict chunk.
/// </summary>
internal enum ConflictResolutionChoice
{
    /// <summary>
    /// Retains the current-side byte slice.
    /// </summary>
    Ours,

    /// <summary>
    /// Retains the incoming-side byte slice.
    /// </summary>
    Theirs,

    /// <summary>
    /// Retains the merge-base byte slice.
    /// </summary>
    Base,

    /// <summary>
    /// Retains the current-side slice followed by the incoming-side slice.
    /// </summary>
    Both,
}
