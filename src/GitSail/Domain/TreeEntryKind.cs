namespace GitSail.Domain;

/// <summary>
/// Identifies one exact Git tree entry kind.
/// </summary>
internal enum TreeEntryKind
{
    /// <summary>
    /// Identifies a nested tree directory.
    /// </summary>
    Tree,

    /// <summary>
    /// Identifies a non-executable regular blob.
    /// </summary>
    RegularFile,

    /// <summary>
    /// Identifies an executable regular blob.
    /// </summary>
    ExecutableFile,

    /// <summary>
    /// Identifies a symbolic-link target stored as a blob.
    /// </summary>
    SymbolicLink,

    /// <summary>
    /// Identifies a submodule commit object.
    /// </summary>
    GitLink,
}
