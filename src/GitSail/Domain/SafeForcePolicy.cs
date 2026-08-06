namespace GitSail.Domain;

/// <summary>
/// Selects whether repository configuration permits any forced remote ref update.
/// </summary>
internal enum SafeForcePolicy
{
    /// <summary>
    /// Permits only new refs, unchanged refs, and fast-forward updates.
    /// </summary>
    Never,

    /// <summary>
    /// Permits forced updates after the product's exact-lease or destructive-confirmation flow.
    /// </summary>
    ExplicitLease,
}
