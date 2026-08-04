using System.Collections.Immutable;

namespace GitSail.Domain;

/// <summary>
/// Contains one exact configured remote name and its effective fetch and push URL sets.
/// </summary>
internal sealed class RemoteInfo
{
    /// <summary>
    /// Initializes one immutable remote configuration record.
    /// </summary>
    /// <param name="name">The exact remote name.</param>
    /// <param name="fetchUrls">The ordered configured fetch URLs.</param>
    /// <param name="pushUrls">The ordered configured push URLs after fetch-URL fallback.</param>
    internal RemoteInfo(
        RemoteName name,
        ImmutableArray<RemoteUrl> fetchUrls,
        ImmutableArray<RemoteUrl> pushUrls)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (fetchUrls.IsDefault)
        {
            throw new ArgumentException("Fetch URLs must be an initialized collection.", nameof(fetchUrls));
        }

        if (pushUrls.IsDefault)
        {
            throw new ArgumentException("Push URLs must be an initialized collection.", nameof(pushUrls));
        }

        Name = name;
        FetchUrls = fetchUrls;
        PushUrls = pushUrls;
    }

    /// <summary>
    /// Gets the exact configured remote name.
    /// </summary>
    internal RemoteName Name { get; }

    /// <summary>
    /// Gets the ordered configured fetch URLs.
    /// </summary>
    internal ImmutableArray<RemoteUrl> FetchUrls { get; }

    /// <summary>
    /// Gets the ordered effective push URLs.
    /// </summary>
    internal ImmutableArray<RemoteUrl> PushUrls { get; }

    /// <summary>
    /// Determines whether another record has byte-identical name and URL configuration.
    /// </summary>
    /// <param name="other">The remote record to compare.</param>
    /// <returns><see langword="true"/> when every execution-relevant value matches.</returns>
    internal bool Matches(RemoteInfo other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Name.Equals(other.Name) &&
            FetchUrls.AsSpan().SequenceEqual(other.FetchUrls.AsSpan()) &&
            PushUrls.AsSpan().SequenceEqual(other.PushUrls.AsSpan());
    }
}
