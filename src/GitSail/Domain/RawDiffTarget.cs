namespace GitSail.Domain;

/// <summary>
/// Identifies the repository side compared against the index by a raw diff operation.
/// </summary>
internal enum RawDiffTarget
{
    /// <summary>
    /// Compares worktree content against the index.
    /// </summary>
    WorkTree,

    /// <summary>
    /// Compares index content against the current commit.
    /// </summary>
    Index,
}
