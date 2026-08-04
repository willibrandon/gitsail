using System.Collections.Immutable;

namespace GitSail.Domain;

/// <summary>
/// Binds Git-resolved default push mappings to exact local and advertised remote state.
/// </summary>
internal sealed class PushPlan
{
    /// <summary>
    /// Initializes one immutable exact push confirmation plan.
    /// </summary>
    /// <param name="catalog">The complete stable remote catalog displayed to the user.</param>
    /// <param name="remote">The exact selected destination remote.</param>
    /// <param name="updates">Every explicit frozen ref update resolved from Git's default behavior.</param>
    /// <param name="upstreamName">The current branch's exact upstream ref, when configured.</param>
    /// <param name="wouldSetUpstream">Whether Git's default dry run requested automatic upstream setup.</param>
    /// <param name="followTags">The configured or explicit follow-tags behavior used to build the plan.</param>
    internal PushPlan(
        RemoteCatalog catalog,
        RemoteInfo remote,
        ImmutableArray<PushUpdatePlan> updates,
        RefName? upstreamName,
        bool wouldSetUpstream,
        GitOptionOverride followTags)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(remote);
        if (updates.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A push plan requires at least one exact update.", nameof(updates));
        }

        var catalogRemote = catalog.Find(remote.Name);
        if (catalogRemote is null || !catalogRemote.Matches(remote))
        {
            throw new ArgumentException(
                "The selected remote must be an exact member of the bound catalog.",
                nameof(remote));
        }

        var destinationUrls = updates[0].Destinations;
        foreach (var update in updates)
        {
            if (update.Destinations.Length != destinationUrls.Length)
            {
                throw new ArgumentException(
                    "Every push update must bind the same ordered destination set.",
                    nameof(updates));
            }

            for (var index = 0; index < destinationUrls.Length; index++)
            {
                if (!update.Destinations[index].Url.Equals(destinationUrls[index].Url))
                {
                    throw new ArgumentException(
                        "Every push update must bind the same ordered destination URLs.",
                        nameof(updates));
                }
            }
        }

        if (!Enum.IsDefined(followTags))
        {
            throw new ArgumentOutOfRangeException(nameof(followTags));
        }

        Catalog = catalog;
        Remote = remote;
        Updates = updates;
        UpstreamName = upstreamName;
        WouldSetUpstream = wouldSetUpstream;
        FollowTags = followTags;
    }

    /// <summary>
    /// Gets the complete stable remote catalog bound to the plan.
    /// </summary>
    internal RemoteCatalog Catalog { get; }

    /// <summary>
    /// Gets the exact selected configured remote.
    /// </summary>
    internal RemoteInfo Remote { get; }

    /// <summary>
    /// Gets every explicit frozen update in Git's resolved order.
    /// </summary>
    internal ImmutableArray<PushUpdatePlan> Updates { get; }

    /// <summary>
    /// Gets the current branch's configured exact upstream ref, when present.
    /// </summary>
    internal RefName? UpstreamName { get; }

    /// <summary>
    /// Gets whether Git's default semantics requested automatic upstream setup.
    /// </summary>
    internal bool WouldSetUpstream { get; }

    /// <summary>
    /// Gets the exact configured or explicit follow-tags behavior used during planning.
    /// </summary>
    internal GitOptionOverride FollowTags { get; }

    /// <summary>
    /// Gets whether any exact destination requires non-fast-forward replacement.
    /// </summary>
    internal bool RequiresForce => Updates.Any(static update => update.RequiresForce);

    /// <summary>
    /// Gets whether any planned mapping deletes a destination ref.
    /// </summary>
    internal bool IncludesDeletion => Updates.Any(static update => update.IsDeletion);
}
