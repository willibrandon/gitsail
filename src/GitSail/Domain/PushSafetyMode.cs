namespace GitSail.Domain;

/// <summary>
/// Selects normal, explicit-lease, or deliberately unleased force behavior for one push.
/// </summary>
internal enum PushSafetyMode
{
    /// <summary>
    /// Permits only updates proven to be fast-forward, new, or already current.
    /// </summary>
    Normal,

    /// <summary>
    /// Permits non-fast-forward updates while protecting every destination with an exact expected OID.
    /// </summary>
    ExplicitLease,

    /// <summary>
    /// Permits unleased force updates only after a separate destructive confirmation.
    /// </summary>
    Force,
}
