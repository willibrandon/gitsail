using System.Collections.Immutable;

namespace GitSail.Domain;

/// <summary>
/// Contains one immutable generation of structured repository status.
/// </summary>
/// <param name="Generation">The operation generation that produced the snapshot.</param>
/// <param name="Repository">The repository identity and locations used for the scan.</param>
/// <param name="HeadObjectId">The current commit, or <see langword="null"/> for an unborn branch.</param>
/// <param name="HeadName">The current branch, or <see langword="null"/> for detached HEAD.</param>
/// <param name="UpstreamName">The configured upstream branch, when present.</param>
/// <param name="AheadCount">The count of local commits not in the upstream.</param>
/// <param name="BehindCount">The count of upstream commits not local.</param>
/// <param name="Entries">The complete structured path status collection.</param>
internal sealed record RepositoryStatusSnapshot(
    OperationGeneration Generation,
    RepositoryLocation Repository,
    ObjectId? HeadObjectId,
    RefName? HeadName,
    RefName? UpstreamName,
    int AheadCount,
    int BehindCount,
    ImmutableArray<RepositoryStatusEntry> Entries)
{
    /// <summary>
    /// Gets the exact stable HEAD and index identity captured around this status generation.
    /// </summary>
    internal RepositoryPrecondition? Precondition { get; init; }
}
