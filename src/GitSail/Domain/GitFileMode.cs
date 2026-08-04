namespace GitSail.Domain;

/// <summary>
/// Identifies one canonical Git index entry mode from an unmerged stage.
/// </summary>
internal enum GitFileMode
{
    /// <summary>
    /// Identifies a non-executable regular file recorded as mode 100644.
    /// </summary>
    RegularFile = 33188,

    /// <summary>
    /// Identifies an executable regular file recorded as mode 100755.
    /// </summary>
    ExecutableFile = 33261,

    /// <summary>
    /// Identifies a symbolic-link blob recorded as mode 120000.
    /// </summary>
    SymbolicLink = 40960,

    /// <summary>
    /// Identifies a submodule commit recorded as mode 160000.
    /// </summary>
    GitLink = 57344,
}
