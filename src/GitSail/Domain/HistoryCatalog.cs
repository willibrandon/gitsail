using System.Collections.Immutable;

namespace GitSail.Domain;

/// <summary>
/// Contains one ordered bounded structured commit-history result.
/// </summary>
/// <param name="Commits">The ordered commits returned by Git.</param>
internal sealed record HistoryCatalog(ImmutableArray<HistoryCommit> Commits);
