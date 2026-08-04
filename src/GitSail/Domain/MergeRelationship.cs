namespace GitSail.Domain;

/// <summary>
/// Describes the exact reachability relationship between current and incoming merge commits.
/// </summary>
internal enum MergeRelationship
{
    /// <summary>
    /// Indicates that the incoming commit is already reachable from current HEAD.
    /// </summary>
    AlreadyIntegrated,

    /// <summary>
    /// Indicates that current HEAD can move directly to the incoming commit.
    /// </summary>
    FastForward,

    /// <summary>
    /// Indicates that both histories contain commits absent from the other side.
    /// </summary>
    Diverged,
}
