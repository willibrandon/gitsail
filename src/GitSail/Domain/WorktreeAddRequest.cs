namespace GitSail.Domain;

/// <summary>
/// Describes one revalidated linked-worktree creation requested by the user.
/// </summary>
/// <param name="TargetDirectory">The absolute or current-worktree-relative target directory.</param>
/// <param name="StartingPoint">The exact displayed branch and object used as the starting point.</param>
/// <param name="Mode">How the new worktree obtains its HEAD.</param>
/// <param name="NewBranchName">The validated new branch name required by new-branch mode.</param>
/// <param name="TrackStartingPoint">Whether a new branch directly tracks its remote starting point.</param>
/// <param name="LockAfterCreation">Whether Git atomically locks the new worktree.</param>
/// <param name="LockReason">The optional literal lock reason.</param>
internal sealed record WorktreeAddRequest(
    string TargetDirectory,
    BranchInfo StartingPoint,
    WorktreeAddMode Mode,
    ValidatedBranchName? NewBranchName,
    bool TrackStartingPoint,
    bool LockAfterCreation,
    string? LockReason);
