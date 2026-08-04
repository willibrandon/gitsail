using System.Collections.Immutable;

namespace GitSail.Domain;

/// <summary>
/// Binds one exact frozen refspec and source OID to every configured push destination.
/// </summary>
internal sealed class PushUpdatePlan
{
    /// <summary>
    /// Initializes one immutable planned ref update.
    /// </summary>
    /// <param name="refSpec">The exact fully qualified source-to-destination mapping.</param>
    /// <param name="sourceObjectId">The exact source OID, or no OID for deletion.</param>
    /// <param name="destinations">Every configured push destination's exact expected state.</param>
    internal PushUpdatePlan(
        PushRefSpec refSpec,
        ObjectId? sourceObjectId,
        ImmutableArray<PushDestinationExpectation> destinations)
    {
        ArgumentNullException.ThrowIfNull(refSpec);
        if (destinations.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A push update requires at least one destination expectation.", nameof(destinations));
        }

        if ((refSpec.Source is null) != (sourceObjectId is null))
        {
            throw new ArgumentException("Push source refs and source OIDs must either both exist or both be absent.");
        }

        RefSpec = refSpec;
        SourceObjectId = sourceObjectId;
        Destinations = destinations;
    }

    /// <summary>
    /// Gets the exact frozen source-to-destination refspec.
    /// </summary>
    internal PushRefSpec RefSpec { get; }

    /// <summary>
    /// Gets the exact local source OID, or no OID for deletion.
    /// </summary>
    internal ObjectId? SourceObjectId { get; }

    /// <summary>
    /// Gets every configured push destination's exact advertised expectation.
    /// </summary>
    internal ImmutableArray<PushDestinationExpectation> Destinations { get; }

    /// <summary>
    /// Gets whether any destination requires a non-fast-forward update.
    /// </summary>
    internal bool RequiresForce => Destinations.Any(static destination =>
        destination.Relationship == PushRelationship.NonFastForward);

    /// <summary>
    /// Gets whether the update deletes a destination ref.
    /// </summary>
    internal bool IsDeletion => RefSpec.Source is null;
}
