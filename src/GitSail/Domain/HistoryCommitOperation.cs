namespace GitSail.Domain;

/// <summary>
/// Identifies the Git operation applied to one exact commit selected from history.
/// </summary>
internal enum HistoryCommitOperation
{
    /// <summary>
    /// Applies the selected commit's change as a new commit on the current branch.
    /// </summary>
    CherryPick,

    /// <summary>
    /// Applies the inverse of the selected commit as a new commit on the current branch.
    /// </summary>
    Revert,
}
