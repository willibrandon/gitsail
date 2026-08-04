using GitSail.Domain;

namespace GitSail.Ui;

/// <summary>
/// Presents one exact worktree row with control-safe branch and state text.
/// </summary>
internal sealed class WorktreeWorkspaceItem
{
    /// <summary>
    /// Initializes one row from an exact Git worktree record.
    /// </summary>
    /// <param name="worktree">The exact worktree record.</param>
    /// <param name="isMain">Whether this is the repository's main worktree.</param>
    internal WorktreeWorkspaceItem(WorktreeInfo worktree, bool isMain)
    {
        ArgumentNullException.ThrowIfNull(worktree);
        Worktree = worktree;
        IsMain = isMain;
    }

    /// <summary>
    /// Gets the exact worktree path used as stable list identity.
    /// </summary>
    internal GitPath Key => Worktree.Path;

    /// <summary>
    /// Gets the complete exact worktree record.
    /// </summary>
    internal WorktreeInfo Worktree { get; }

    /// <summary>
    /// Gets whether this row represents the repository's main worktree.
    /// </summary>
    internal bool IsMain { get; }

    /// <summary>
    /// Formats the control-safe path, HEAD state, and management markers.
    /// </summary>
    /// <returns>The bounded list-row presentation.</returns>
    public override string ToString()
    {
        var head = Worktree.IsBare
            ? "bare"
            : Worktree.BranchName?.DisplayText ?? "detached";
        var markers = new List<string>();
        if (IsMain)
        {
            markers.Add("main");
        }

        if (Worktree.IsLocked)
        {
            markers.Add("locked");
        }

        if (Worktree.IsPrunable)
        {
            markers.Add("prunable");
        }

        var suffix = markers.Count == 0 ? string.Empty : $" [{string.Join(", ", markers)}]";
        return $"{Worktree.Path.DisplayText} | {head}{suffix}";
    }
}
