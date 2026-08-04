namespace GitSail.Domain;

/// <summary>
/// Identifies one index or worktree status reported by Git porcelain version 2.
/// </summary>
internal enum GitFileStatus
{
    /// <summary>
    /// Indicates no change in the corresponding state.
    /// </summary>
    Unmodified,

    /// <summary>
    /// Indicates modified content.
    /// </summary>
    Modified,

    /// <summary>
    /// Indicates added content.
    /// </summary>
    Added,

    /// <summary>
    /// Indicates deleted content.
    /// </summary>
    Deleted,

    /// <summary>
    /// Indicates a renamed path.
    /// </summary>
    Renamed,

    /// <summary>
    /// Indicates a copied path.
    /// </summary>
    Copied,

    /// <summary>
    /// Indicates a file-type change.
    /// </summary>
    TypeChanged,

    /// <summary>
    /// Indicates an unmerged state.
    /// </summary>
    Unmerged,

    /// <summary>
    /// Indicates an untracked path.
    /// </summary>
    Untracked,

    /// <summary>
    /// Indicates an ignored path.
    /// </summary>
    Ignored,
}
