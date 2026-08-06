namespace GitSail.Git.Execution;

/// <summary>
/// Identifies the explicit user decision for one executable-configuration review.
/// </summary>
internal enum ExecutableCapabilityDecision
{
    /// <summary>
    /// Denies the requested configured command without persisting a grant.
    /// </summary>
    Deny,

    /// <summary>
    /// Allows only the exact pending invocation.
    /// </summary>
    AllowOnce,

    /// <summary>
    /// Allows the exact command fingerprint for the current repository identity.
    /// </summary>
    AllowRepository,
}
