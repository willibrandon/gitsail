namespace GitSail.Ui;

/// <summary>
/// Identifies the status file pane that owns the current row focus.
/// </summary>
internal enum StatusWorkspacePane
{
    /// <summary>
    /// Identifies the worktree and untracked file pane.
    /// </summary>
    Unstaged,

    /// <summary>
    /// Identifies the index file pane.
    /// </summary>
    Staged,
}
