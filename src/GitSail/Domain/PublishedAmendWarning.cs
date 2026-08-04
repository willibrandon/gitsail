using System.Collections.Immutable;

namespace GitSail.Domain;

/// <summary>
/// Describes local remote-tracking refs that contain the commit selected for amendment.
/// </summary>
internal sealed record PublishedAmendWarning
{
    /// <summary>
    /// Initializes one published-amend warning from the complete matching reference set.
    /// </summary>
    /// <param name="remoteTrackingRefs">The nonempty exact local remote-tracking reference names.</param>
    internal PublishedAmendWarning(ImmutableArray<RefName> remoteTrackingRefs)
    {
        if (remoteTrackingRefs.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "A published-amend warning requires at least one remote-tracking reference.",
                nameof(remoteTrackingRefs));
        }

        RemoteTrackingRefs = remoteTrackingRefs;
    }

    /// <summary>
    /// Gets every nonsymbolic local remote-tracking ref that contains the selected commit.
    /// </summary>
    internal ImmutableArray<RefName> RemoteTrackingRefs { get; }

    /// <summary>
    /// Determines whether another warning contains the same complete ordered local reference set.
    /// </summary>
    /// <param name="other">The warning independently captured for comparison.</param>
    /// <returns><see langword="true"/> only when both warnings contain exactly the same refs.</returns>
    internal bool Matches(PublishedAmendWarning? other)
        => other is not null && RemoteTrackingRefs.SequenceEqual(other.RemoteTrackingRefs);
}
