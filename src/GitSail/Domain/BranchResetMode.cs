namespace GitSail.Domain;

/// <summary>
/// Selects the index and worktree effects of resetting the current branch.
/// </summary>
internal enum BranchResetMode
{
    /// <summary>
    /// Moves HEAD while preserving the index and worktree.
    /// </summary>
    Soft,

    /// <summary>
    /// Moves HEAD and resets the index while preserving worktree files.
    /// </summary>
    Mixed,

    /// <summary>
    /// Moves HEAD and resets both the index and tracked worktree files.
    /// </summary>
    Hard,
}
