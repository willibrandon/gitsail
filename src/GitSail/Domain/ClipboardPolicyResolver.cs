namespace GitSail.Domain;

/// <summary>
/// Resolves the typed clipboard policy from one complete Git configuration snapshot.
/// </summary>
internal static class ClipboardPolicyResolver
{
    /// <summary>
    /// Resolves the effective policy while failing closed for an invalid explicit value.
    /// </summary>
    /// <param name="configuration">The complete ordered configuration snapshot.</param>
    /// <returns>The configured clipboard policy, or the safe default.</returns>
    internal static ClipboardPolicy Resolve(GitConfigurationSnapshot configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var resolved = configuration.Resolve("gitsail.clipboard", GitConfigurationScope.Local);
        return resolved.EffectiveParsedValue?.Text?.ToLowerInvariant() switch
        {
            "auto" => ClipboardPolicy.Auto,
            "osc52" => ClipboardPolicy.Osc52,
            "helper" => ClipboardPolicy.Helper,
            _ => ClipboardPolicy.Off,
        };
    }
}
