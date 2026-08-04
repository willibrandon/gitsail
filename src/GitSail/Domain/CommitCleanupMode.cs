namespace GitSail.Domain;

/// <summary>
/// Selects one documented Git commit-message cleanup policy.
/// </summary>
internal enum CommitCleanupMode
{
    /// <summary>
    /// Uses Git's context-dependent default cleanup behavior.
    /// </summary>
    Default,

    /// <summary>
    /// Removes commentary, surrounding blank lines, and trailing whitespace.
    /// </summary>
    Strip,

    /// <summary>
    /// Retains commentary while cleaning whitespace and consecutive blank lines.
    /// </summary>
    Whitespace,

    /// <summary>
    /// Preserves the supplied message bytes without cleanup.
    /// </summary>
    Verbatim,

    /// <summary>
    /// Applies whitespace cleanup and truncates at Git's scissors marker.
    /// </summary>
    Scissors,
}
