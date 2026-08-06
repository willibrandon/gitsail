namespace GitSail.Domain;

/// <summary>
/// Resolves the typed force-push ceiling from one complete Git configuration snapshot.
/// </summary>
internal static class SafeForcePolicyResolver
{
    /// <summary>
    /// Resolves the effective policy while failing closed for an invalid explicit value.
    /// </summary>
    /// <param name="configuration">The complete ordered configuration snapshot.</param>
    /// <returns>The configured policy, or the safe default.</returns>
    internal static SafeForcePolicy Resolve(GitConfigurationSnapshot configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var resolved = configuration.Resolve(
            "gitsail.safeforcepolicy",
            GitConfigurationScope.Local);
        return string.Equals(
            resolved.EffectiveParsedValue?.Text,
            "explicit-lease",
            StringComparison.OrdinalIgnoreCase)
                ? SafeForcePolicy.ExplicitLease
                : SafeForcePolicy.Never;
    }
}
