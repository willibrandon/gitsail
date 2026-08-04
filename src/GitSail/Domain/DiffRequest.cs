using System.Collections.Immutable;

namespace GitSail.Domain;

/// <summary>
/// Describes one validated comparison using exact commit identities and native pathspecs.
/// </summary>
internal sealed record DiffRequest
{
    private DiffRequest(
        DiffComparisonKind kind,
        ObjectId? leftCommit,
        ObjectId? rightCommit,
        ImmutableArray<GitPath> pathspecs)
    {
        Kind = kind;
        LeftCommit = leftCommit;
        RightCommit = rightCommit;
        Pathspecs = pathspecs.IsDefault ? [] : pathspecs;
    }

    /// <summary>
    /// Gets the repository-state pairing selected for this comparison.
    /// </summary>
    internal DiffComparisonKind Kind { get; }

    /// <summary>
    /// Gets the exact left commit when this comparison uses one.
    /// </summary>
    internal ObjectId? LeftCommit { get; }

    /// <summary>
    /// Gets the exact right commit for a commit-to-commit comparison.
    /// </summary>
    internal ObjectId? RightCommit { get; }

    /// <summary>
    /// Gets the exact native pathspecs limiting this comparison.
    /// </summary>
    internal ImmutableArray<GitPath> Pathspecs { get; }

    /// <summary>
    /// Creates an index-to-worktree comparison.
    /// </summary>
    /// <param name="pathspecs">The exact optional native pathspecs.</param>
    /// <returns>The validated worktree comparison request.</returns>
    internal static DiffRequest IndexToWorkTree(ImmutableArray<GitPath> pathspecs)
        => new(DiffComparisonKind.IndexToWorkTree, null, null, pathspecs);

    /// <summary>
    /// Creates a current-commit-to-index comparison.
    /// </summary>
    /// <param name="pathspecs">The exact optional native pathspecs.</param>
    /// <returns>The validated staged comparison request.</returns>
    internal static DiffRequest HeadToIndex(ImmutableArray<GitPath> pathspecs)
        => new(DiffComparisonKind.HeadToIndex, null, null, pathspecs);

    /// <summary>
    /// Creates an exact-commit-to-worktree comparison.
    /// </summary>
    /// <param name="leftCommit">The exact commit on the left side.</param>
    /// <param name="pathspecs">The exact optional native pathspecs.</param>
    /// <returns>The validated worktree comparison request.</returns>
    internal static DiffRequest CommitToWorkTree(
        ObjectId leftCommit,
        ImmutableArray<GitPath> pathspecs)
        => new(
            DiffComparisonKind.CommitToWorkTree,
            leftCommit ?? throw new ArgumentNullException(nameof(leftCommit)),
            null,
            pathspecs);

    /// <summary>
    /// Creates an exact-commit-to-index comparison.
    /// </summary>
    /// <param name="leftCommit">The exact commit on the left side.</param>
    /// <param name="pathspecs">The exact optional native pathspecs.</param>
    /// <returns>The validated staged comparison request.</returns>
    internal static DiffRequest CommitToIndex(
        ObjectId leftCommit,
        ImmutableArray<GitPath> pathspecs)
        => new(
            DiffComparisonKind.CommitToIndex,
            leftCommit ?? throw new ArgumentNullException(nameof(leftCommit)),
            null,
            pathspecs);

    /// <summary>
    /// Creates an exact commit-to-commit comparison.
    /// </summary>
    /// <param name="leftCommit">The exact commit on the left side.</param>
    /// <param name="rightCommit">The exact commit on the right side.</param>
    /// <param name="pathspecs">The exact optional native pathspecs.</param>
    /// <returns>The validated immutable comparison request.</returns>
    internal static DiffRequest CommitToCommit(
        ObjectId leftCommit,
        ObjectId rightCommit,
        ImmutableArray<GitPath> pathspecs)
        => new(
            DiffComparisonKind.CommitToCommit,
            leftCommit ?? throw new ArgumentNullException(nameof(leftCommit)),
            rightCommit ?? throw new ArgumentNullException(nameof(rightCommit)),
            pathspecs);
}
