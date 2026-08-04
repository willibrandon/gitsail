namespace GitSail.Domain;

/// <summary>
/// Identifies the structural kind of one interactive-rebase todo line.
/// </summary>
internal enum RebaseTodoLineKind
{
    /// <summary>
    /// Identifies an empty or whitespace-only line.
    /// </summary>
    Blank,

    /// <summary>
    /// Identifies a comment line ignored by Git.
    /// </summary>
    Comment,

    /// <summary>
    /// Identifies a validated sequencer command line.
    /// </summary>
    Command,
}
