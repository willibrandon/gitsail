using System.Collections.Immutable;

namespace GitSail.Domain;

/// <summary>
/// Contains one bounded structured-history request.
/// </summary>
/// <param name="RevisionRange">The optional literal revision range.</param>
/// <param name="Pathspecs">The exact native pathspecs restricting history.</param>
/// <param name="MaximumCommitCount">The maximum number of commits returned by Git.</param>
internal sealed record HistoryQuery(
    Revision? RevisionRange,
    ImmutableArray<GitPath> Pathspecs,
    int MaximumCommitCount)
{
    /// <summary>
    /// Creates the default history request for the current repository.
    /// </summary>
    /// <returns>A request for the first 2,000 commits reachable from HEAD.</returns>
    internal static HistoryQuery CreateDefault()
        => new(RevisionRange: null, Pathspecs: [], MaximumCommitCount: 2_000);
}
