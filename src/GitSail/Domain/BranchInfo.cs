using System.Collections.Immutable;

namespace GitSail.Domain;

/// <summary>
/// Describes one exact local or remote-tracking branch captured for user action.
/// </summary>
internal sealed class BranchInfo
{
    /// <summary>
    /// Initializes one immutable branch record.
    /// </summary>
    /// <param name="fullName">The complete exact ref name.</param>
    /// <param name="shortName">The exact name with its branch namespace removed.</param>
    /// <param name="kind">The local or remote-tracking namespace kind.</param>
    /// <param name="targetObjectId">The exact object currently named by the ref.</param>
    /// <param name="upstreamName">The exact configured upstream ref, when present.</param>
    /// <param name="aheadCount">The local commits not reachable from the upstream.</param>
    /// <param name="behindCount">The upstream commits not reachable locally.</param>
    /// <param name="isUpstreamGone">Whether the configured upstream ref no longer exists.</param>
    /// <param name="isCurrent">Whether this is the current worktree's attached HEAD.</param>
    /// <param name="occupiedWorktrees">Every worktree path currently using this local branch.</param>
    /// <param name="symbolicTarget">The exact symbolic target for refs such as a remote HEAD.</param>
    internal BranchInfo(
        RefName fullName,
        RefName shortName,
        BranchKind kind,
        ObjectId targetObjectId,
        RefName? upstreamName,
        int aheadCount,
        int behindCount,
        bool isUpstreamGone,
        bool isCurrent,
        ImmutableArray<GitPath> occupiedWorktrees,
        RefName? symbolicTarget)
    {
        ArgumentNullException.ThrowIfNull(fullName);
        ArgumentNullException.ThrowIfNull(shortName);
        ArgumentNullException.ThrowIfNull(targetObjectId);
        if (aheadCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(aheadCount));
        }

        if (behindCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(behindCount));
        }

        FullName = fullName;
        ShortName = shortName;
        Kind = kind;
        TargetObjectId = targetObjectId;
        UpstreamName = upstreamName;
        AheadCount = aheadCount;
        BehindCount = behindCount;
        IsUpstreamGone = isUpstreamGone;
        IsCurrent = isCurrent;
        OccupiedWorktrees = occupiedWorktrees;
        SymbolicTarget = symbolicTarget;
    }

    /// <summary>
    /// Gets the complete exact ref name.
    /// </summary>
    internal RefName FullName { get; }

    /// <summary>
    /// Gets the exact name with its branch namespace removed.
    /// </summary>
    internal RefName ShortName { get; }

    /// <summary>
    /// Gets the local or remote-tracking namespace kind.
    /// </summary>
    internal BranchKind Kind { get; }

    /// <summary>
    /// Gets the exact object currently named by the ref.
    /// </summary>
    internal ObjectId TargetObjectId { get; }

    /// <summary>
    /// Gets the exact configured upstream ref, when present.
    /// </summary>
    internal RefName? UpstreamName { get; }

    /// <summary>
    /// Gets the local commits not reachable from the upstream.
    /// </summary>
    internal int AheadCount { get; }

    /// <summary>
    /// Gets the upstream commits not reachable locally.
    /// </summary>
    internal int BehindCount { get; }

    /// <summary>
    /// Gets whether the configured upstream ref no longer exists.
    /// </summary>
    internal bool IsUpstreamGone { get; }

    /// <summary>
    /// Gets whether this is the current worktree's attached HEAD.
    /// </summary>
    internal bool IsCurrent { get; }

    /// <summary>
    /// Gets every exact worktree path currently using this local branch.
    /// </summary>
    internal ImmutableArray<GitPath> OccupiedWorktrees { get; }

    /// <summary>
    /// Gets the exact symbolic target for refs such as a remote HEAD.
    /// </summary>
    internal RefName? SymbolicTarget { get; }

    /// <summary>
    /// Determines whether another record has the same exact action-relevant identity.
    /// </summary>
    /// <param name="other">The independently captured branch record.</param>
    /// <returns><see langword="true"/> when every retained field is identical.</returns>
    internal bool Matches(BranchInfo other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return FullName.Equals(other.FullName) &&
            ShortName.Equals(other.ShortName) &&
            Kind == other.Kind &&
            TargetObjectId.Equals(other.TargetObjectId) &&
            Equals(UpstreamName, other.UpstreamName) &&
            AheadCount == other.AheadCount &&
            BehindCount == other.BehindCount &&
            IsUpstreamGone == other.IsUpstreamGone &&
            IsCurrent == other.IsCurrent &&
            OccupiedWorktrees.SequenceEqual(other.OccupiedWorktrees) &&
            Equals(SymbolicTarget, other.SymbolicTarget);
    }
}
