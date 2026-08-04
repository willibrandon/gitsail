using System.Collections.Immutable;

namespace GitSail.Domain;

/// <summary>
/// Contains exact ref mappings and automatic-upstream intent parsed from Git push porcelain output.
/// </summary>
internal sealed class PushPorcelainResult
{
    /// <summary>
    /// Initializes one immutable parsed default-push response.
    /// </summary>
    /// <param name="refSpecs">The exact fully qualified push mappings.</param>
    /// <param name="wouldSetUpstream">Whether Git reported automatic upstream setup.</param>
    internal PushPorcelainResult(
        ImmutableArray<PushRefSpec> refSpecs,
        bool wouldSetUpstream)
    {
        if (refSpecs.IsDefault)
        {
            throw new ArgumentException("Push refspecs must be an initialized collection.", nameof(refSpecs));
        }

        RefSpecs = refSpecs;
        WouldSetUpstream = wouldSetUpstream;
    }

    /// <summary>
    /// Gets the exact fully qualified ref mappings in Git's reported order.
    /// </summary>
    internal ImmutableArray<PushRefSpec> RefSpecs { get; }

    /// <summary>
    /// Gets whether Git reported that the default push would establish upstream tracking.
    /// </summary>
    internal bool WouldSetUpstream { get; }
}
