namespace GitSail.Domain;

/// <summary>
/// Identifies the Git porcelain record shape represented by a status entry.
/// </summary>
internal enum RepositoryStatusEntryKind
{
    /// <summary>
    /// Identifies an ordinary tracked-path change.
    /// </summary>
    Ordinary,

    /// <summary>
    /// Identifies a rename with an original path.
    /// </summary>
    Rename,

    /// <summary>
    /// Identifies a copy with an original path.
    /// </summary>
    Copy,

    /// <summary>
    /// Identifies an unmerged path.
    /// </summary>
    Unmerged,

    /// <summary>
    /// Identifies an untracked path.
    /// </summary>
    Untracked,

    /// <summary>
    /// Identifies an ignored path.
    /// </summary>
    Ignored,
}
