namespace GitSail.Domain;

/// <summary>
/// Identifies how a new linked worktree obtains its HEAD.
/// </summary>
internal enum WorktreeAddMode
{
    /// <summary>
    /// Checks out one existing unoccupied local branch.
    /// </summary>
    ExistingBranch,

    /// <summary>
    /// Creates and checks out one new local branch.
    /// </summary>
    NewBranch,

    /// <summary>
    /// Checks out the selected commit with detached HEAD.
    /// </summary>
    Detached,
}
