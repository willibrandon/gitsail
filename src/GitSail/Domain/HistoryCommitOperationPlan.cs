namespace GitSail.Domain;

/// <summary>
/// Captures the exact commit, parent choice, and repository state shown for confirmation.
/// </summary>
/// <param name="Operation">The requested cherry-pick or commit-revert operation.</param>
/// <param name="Commit">The exact immutable commit selected from history.</param>
/// <param name="MainlineParent">The selected one-based mainline parent for a merge commit.</param>
/// <param name="Precondition">The displayed HEAD, attachment, and index identity.</param>
/// <param name="WorktreeFingerprint">The displayed tracked and untracked worktree identity.</param>
internal sealed record HistoryCommitOperationPlan(
    HistoryCommitOperation Operation,
    ObjectId Commit,
    int? MainlineParent,
    RepositoryPrecondition Precondition,
    RepositoryWorktreeFingerprint WorktreeFingerprint);
