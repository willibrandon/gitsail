namespace GitSail.Domain;

/// <summary>
/// Captures the exact revisions and repository state shown before an interactive rebase.
/// </summary>
/// <param name="Head">The exact current HEAD commit to rewrite.</param>
/// <param name="Upstream">The exact upstream commit excluded from the todo range.</param>
/// <param name="Onto">The exact new base commit.</param>
/// <param name="CommitCount">The number of commits Git will place in the initial todo.</param>
/// <param name="Precondition">The displayed HEAD, attachment, and index identity.</param>
/// <param name="WorktreeFingerprint">The displayed tracked and untracked worktree identity.</param>
internal sealed record RebasePlan(
    ObjectId Head,
    ObjectId Upstream,
    ObjectId Onto,
    int CommitCount,
    RepositoryPrecondition Precondition,
    RepositoryWorktreeFingerprint WorktreeFingerprint);
