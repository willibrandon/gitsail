using System.Collections.Immutable;

namespace GitSail.Domain;

/// <summary>
/// Contains one stable branch and linked-worktree snapshot prepared for user action.
/// </summary>
internal sealed class BranchCatalog
{
    /// <summary>
    /// Initializes one immutable stable branch catalog.
    /// </summary>
    /// <param name="precondition">The exact repository precondition captured with the catalog.</param>
    /// <param name="branches">The complete local and remote-tracking branch records.</param>
    /// <param name="worktrees">The complete linked-worktree records.</param>
    internal BranchCatalog(
        RepositoryPrecondition precondition,
        ImmutableArray<BranchInfo> branches,
        ImmutableArray<WorktreeInfo> worktrees)
    {
        ArgumentNullException.ThrowIfNull(precondition);
        Precondition = precondition;
        Branches = branches;
        Worktrees = worktrees;
    }

    /// <summary>
    /// Gets the exact repository precondition captured with the catalog.
    /// </summary>
    internal RepositoryPrecondition Precondition { get; }

    /// <summary>
    /// Gets the complete local and remote-tracking branch records.
    /// </summary>
    internal ImmutableArray<BranchInfo> Branches { get; }

    /// <summary>
    /// Gets the complete linked-worktree records.
    /// </summary>
    internal ImmutableArray<WorktreeInfo> Worktrees { get; }

    /// <summary>
    /// Finds one branch by its complete exact ref name.
    /// </summary>
    /// <param name="fullName">The complete exact ref name.</param>
    /// <returns>The matching branch, or <see langword="null"/> when absent.</returns>
    internal BranchInfo? Find(RefName fullName)
    {
        ArgumentNullException.ThrowIfNull(fullName);
        return Branches.FirstOrDefault(branch => branch.FullName.Equals(fullName));
    }
}
