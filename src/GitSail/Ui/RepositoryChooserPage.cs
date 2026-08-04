namespace GitSail.Ui;

/// <summary>
/// Identifies the active repository chooser workflow.
/// </summary>
internal enum RepositoryChooserPage
{
    /// <summary>
    /// Opens the repository containing an entered directory.
    /// </summary>
    Open,

    /// <summary>
    /// Opens one exact recently used repository.
    /// </summary>
    Recent,

    /// <summary>
    /// Clones a local or remote repository.
    /// </summary>
    Clone,

    /// <summary>
    /// Initializes a repository with a worktree.
    /// </summary>
    Initialize,

    /// <summary>
    /// Initializes a bare repository without a worktree.
    /// </summary>
    InitializeBare,

    /// <summary>
    /// Opens an existing main or linked worktree directly.
    /// </summary>
    OpenWorktree,
}
