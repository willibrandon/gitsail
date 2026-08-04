namespace GitSail.Domain;

/// <summary>
/// Describes one exact linked-worktree record reported by Git porcelain.
/// </summary>
internal sealed class WorktreeInfo
{
    /// <summary>
    /// Initializes one immutable linked-worktree record.
    /// </summary>
    /// <param name="path">The exact native worktree path.</param>
    /// <param name="headObjectId">The checked-out commit, when Git reports one.</param>
    /// <param name="branchName">The exact checked-out local ref, or <see langword="null"/> when detached.</param>
    /// <param name="isBare">Whether the worktree record represents a bare repository.</param>
    /// <param name="isLocked">Whether Git reports the worktree as locked.</param>
    /// <param name="lockReasonDisplay">The control-safe lock reason intended only for display.</param>
    /// <param name="isPrunable">Whether Git reports the administrative entry as prunable.</param>
    /// <param name="prunableReasonDisplay">The control-safe prune reason intended only for display.</param>
    internal WorktreeInfo(
        GitPath path,
        ObjectId? headObjectId,
        RefName? branchName,
        bool isBare,
        bool isLocked,
        string? lockReasonDisplay,
        bool isPrunable,
        string? prunableReasonDisplay)
    {
        ArgumentNullException.ThrowIfNull(path);
        Path = path;
        HeadObjectId = headObjectId;
        BranchName = branchName;
        IsBare = isBare;
        IsLocked = isLocked;
        LockReasonDisplay = lockReasonDisplay;
        IsPrunable = isPrunable;
        PrunableReasonDisplay = prunableReasonDisplay;
    }

    /// <summary>
    /// Gets the exact native worktree path.
    /// </summary>
    internal GitPath Path { get; }

    /// <summary>
    /// Gets the checked-out commit, when Git reports one.
    /// </summary>
    internal ObjectId? HeadObjectId { get; }

    /// <summary>
    /// Gets the exact checked-out local ref, or <see langword="null"/> when detached.
    /// </summary>
    internal RefName? BranchName { get; }

    /// <summary>
    /// Gets whether the worktree record represents a bare repository.
    /// </summary>
    internal bool IsBare { get; }

    /// <summary>
    /// Gets whether Git reports the worktree as locked.
    /// </summary>
    internal bool IsLocked { get; }

    /// <summary>
    /// Gets the control-safe lock reason intended only for display.
    /// </summary>
    internal string? LockReasonDisplay { get; }

    /// <summary>
    /// Gets whether Git reports the administrative entry as prunable.
    /// </summary>
    internal bool IsPrunable { get; }

    /// <summary>
    /// Gets the control-safe prune reason intended only for display.
    /// </summary>
    internal string? PrunableReasonDisplay { get; }

    /// <summary>
    /// Determines whether another record has the same exact mutation-relevant identity.
    /// </summary>
    /// <param name="other">The independently captured worktree record.</param>
    /// <returns><see langword="true"/> when every retained field is identical.</returns>
    internal bool Matches(WorktreeInfo other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Path.Equals(other.Path) &&
            Equals(HeadObjectId, other.HeadObjectId) &&
            Equals(BranchName, other.BranchName) &&
            IsBare == other.IsBare &&
            IsLocked == other.IsLocked &&
            string.Equals(LockReasonDisplay, other.LockReasonDisplay, StringComparison.Ordinal) &&
            IsPrunable == other.IsPrunable &&
            string.Equals(PrunableReasonDisplay, other.PrunableReasonDisplay, StringComparison.Ordinal);
    }
}
