namespace GitSail.Ui;

/// <summary>
/// Identifies a repository view requested from the main workspace.
/// The shell opens the requested view and then returns to the repository workspace.
/// </summary>
internal enum RepositoryWorkspaceDestination
{
    /// <summary>
    /// Opens the searchable commit graph and commit-operation view.
    /// Returning from history reopens the same repository workspace.
    /// </summary>
    History,

    /// <summary>
    /// Opens the revision tree browser for the current repository.
    /// Returning from the browser reopens the same repository workspace.
    /// </summary>
    Browser,
}
