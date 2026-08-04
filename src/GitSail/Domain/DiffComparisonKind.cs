namespace GitSail.Domain;

/// <summary>
/// Identifies the two repository states used by one immutable comparison.
/// </summary>
internal enum DiffComparisonKind
{
    /// <summary>
    /// Compares the index with the current worktree.
    /// </summary>
    IndexToWorkTree,

    /// <summary>
    /// Compares the current commit with the index.
    /// </summary>
    HeadToIndex,

    /// <summary>
    /// Compares one exact commit with the current worktree.
    /// </summary>
    CommitToWorkTree,

    /// <summary>
    /// Compares one exact commit with the current index.
    /// </summary>
    CommitToIndex,

    /// <summary>
    /// Compares two exact commits.
    /// </summary>
    CommitToCommit,
}
