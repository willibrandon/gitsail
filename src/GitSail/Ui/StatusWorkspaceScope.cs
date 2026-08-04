namespace GitSail.Ui;

/// <summary>
/// Selects which structured status records are presented in a repository workspace.
/// </summary>
internal enum StatusWorkspaceScope
{
    /// <summary>
    /// Presents every staged, unstaged, untracked, and unmerged path returned by Git.
    /// </summary>
    AllChanges,

    /// <summary>
    /// Presents only unresolved index entries in one conflict list.
    /// </summary>
    UnmergedOnly,
}
