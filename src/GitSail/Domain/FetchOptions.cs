namespace GitSail.Domain;

/// <summary>
/// Contains validated typed pruning and tag behavior for one Git fetch transaction.
/// </summary>
internal sealed class FetchOptions
{
    /// <summary>
    /// Initializes one immutable fetch-option set from allowlisted values.
    /// </summary>
    /// <param name="prune">The configured or explicit stale-ref pruning behavior.</param>
    /// <param name="tags">The configured or explicit tag-fetch behavior.</param>
    internal FetchOptions(GitOptionOverride prune, FetchTagMode tags)
    {
        if (!Enum.IsDefined(prune))
        {
            throw new ArgumentOutOfRangeException(nameof(prune));
        }

        if (!Enum.IsDefined(tags))
        {
            throw new ArgumentOutOfRangeException(nameof(tags));
        }

        Prune = prune;
        Tags = tags;
    }

    /// <summary>
    /// Gets the configured or explicit stale-ref pruning behavior.
    /// </summary>
    internal GitOptionOverride Prune { get; }

    /// <summary>
    /// Gets the configured or explicit tag-fetch behavior.
    /// </summary>
    internal FetchTagMode Tags { get; }

    /// <summary>
    /// Creates a fetch option set that honors all effective Git configuration.
    /// </summary>
    /// <returns>The configuration-honoring default options.</returns>
    internal static FetchOptions CreateDefault()
        => new(GitOptionOverride.Configured, FetchTagMode.Configured);
}
