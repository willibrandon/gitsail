using System.Collections.Immutable;

namespace GitSail.Domain;

/// <summary>
/// Captures the exact displayed worktree and cleanliness reviewed before removal.
/// </summary>
/// <param name="Catalog">The stable branch and worktree catalog shown to the user.</param>
/// <param name="Worktree">The exact linked worktree selected for removal.</param>
/// <param name="Status">The exact porcelain status including tracked, untracked, and ignored paths.</param>
/// <param name="SubmoduleStatus">The exact recursive submodule inventory.</param>
internal sealed record WorktreeRemovalPlan(
    BranchCatalog Catalog,
    WorktreeInfo Worktree,
    ImmutableArray<byte> Status,
    ImmutableArray<byte> SubmoduleStatus)
{
    /// <summary>
    /// Gets whether Git reported no tracked, untracked, or ignored paths.
    /// </summary>
    internal bool IsClean => Status.IsEmpty;

    /// <summary>
    /// Gets whether the worktree contains any configured submodule entry.
    /// </summary>
    internal bool HasSubmodules => !SubmoduleStatus.IsEmpty;

    /// <summary>
    /// Gets whether Git requires an explicitly confirmed force removal.
    /// </summary>
    internal bool RequiresForce => !IsClean || HasSubmodules;
}
