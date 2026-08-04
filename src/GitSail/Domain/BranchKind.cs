namespace GitSail.Domain;

/// <summary>
/// Identifies whether a branch ref is local or remote-tracking.
/// </summary>
internal enum BranchKind
{
    /// <summary>
    /// Identifies a ref below <c>refs/heads/</c>.
    /// </summary>
    Local,

    /// <summary>
    /// Identifies a ref below <c>refs/remotes/</c>.
    /// </summary>
    RemoteTracking,
}
