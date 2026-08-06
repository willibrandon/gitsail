namespace GitSail.Domain;

/// <summary>
/// Resolves structured trace verbosity from one complete Git configuration snapshot.
/// </summary>
internal static class GitSailLogLevelResolver
{
    /// <summary>
    /// Resolves the effective log level while disabling output for an invalid explicit value.
    /// </summary>
    /// <param name="configuration">The complete ordered configuration snapshot.</param>
    /// <returns>The configured minimum trace severity.</returns>
    internal static GitSailLogLevel Resolve(GitConfigurationSnapshot configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var resolved = configuration.Resolve("gitsail.loglevel", GitConfigurationScope.Local);
        return resolved.EffectiveParsedValue?.Text?.ToLowerInvariant() switch
        {
            "trace" => GitSailLogLevel.Trace,
            "debug" => GitSailLogLevel.Debug,
            "information" => GitSailLogLevel.Information,
            "warning" => GitSailLogLevel.Warning,
            "error" => GitSailLogLevel.Error,
            "critical" => GitSailLogLevel.Critical,
            "none" => GitSailLogLevel.None,
            _ => GitSailLogLevel.None,
        };
    }
}
