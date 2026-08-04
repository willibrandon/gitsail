namespace GitSail.Domain;

/// <summary>
/// Binds a displayed merge source and reachability preview to exact repository bytes.
/// </summary>
internal sealed class MergePlan
{
    /// <summary>
    /// Initializes one immutable exact merge confirmation snapshot.
    /// </summary>
    /// <param name="precondition">The exact current HEAD attachment, object, and index.</param>
    /// <param name="worktreeFingerprint">The action-relevant exact worktree fingerprint.</param>
    /// <param name="source">The exact selected branch and target object.</param>
    /// <param name="relationship">The exact current-to-incoming reachability relationship.</param>
    /// <param name="currentOnlyCommitCount">The current-side commits absent from the incoming side.</param>
    /// <param name="incomingCommitCount">The incoming-side commits absent from current HEAD.</param>
    internal MergePlan(
        RepositoryPrecondition precondition,
        RepositoryWorktreeFingerprint worktreeFingerprint,
        BranchInfo source,
        MergeRelationship relationship,
        int currentOnlyCommitCount,
        int incomingCommitCount)
    {
        ArgumentNullException.ThrowIfNull(precondition);
        ArgumentNullException.ThrowIfNull(worktreeFingerprint);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(currentOnlyCommitCount);
        ArgumentOutOfRangeException.ThrowIfNegative(incomingCommitCount);
        if (!Enum.IsDefined(relationship))
        {
            throw new ArgumentOutOfRangeException(nameof(relationship));
        }

        Precondition = precondition;
        WorktreeFingerprint = worktreeFingerprint;
        Source = source;
        Relationship = relationship;
        CurrentOnlyCommitCount = currentOnlyCommitCount;
        IncomingCommitCount = incomingCommitCount;
    }

    /// <summary>
    /// Gets the exact current HEAD attachment, object, and index.
    /// </summary>
    internal RepositoryPrecondition Precondition { get; }

    /// <summary>
    /// Gets the action-relevant exact worktree fingerprint.
    /// </summary>
    internal RepositoryWorktreeFingerprint WorktreeFingerprint { get; }

    /// <summary>
    /// Gets the exact selected branch and target object.
    /// </summary>
    internal BranchInfo Source { get; }

    /// <summary>
    /// Gets the exact current-to-incoming reachability relationship.
    /// </summary>
    internal MergeRelationship Relationship { get; }

    /// <summary>
    /// Gets the current-side commits absent from the incoming side.
    /// </summary>
    internal int CurrentOnlyCommitCount { get; }

    /// <summary>
    /// Gets the incoming-side commits absent from current HEAD.
    /// </summary>
    internal int IncomingCommitCount { get; }
}
