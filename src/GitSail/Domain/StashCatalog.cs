using System.Collections.Immutable;

namespace GitSail.Domain;

/// <summary>
/// Contains one stable stash reflog and complete worktree snapshot prepared for action.
/// </summary>
internal sealed class StashCatalog
{
    /// <summary>
    /// Initializes one immutable stable stash catalog.
    /// </summary>
    /// <param name="precondition">The exact HEAD and index state captured with the catalog.</param>
    /// <param name="worktreeFingerprint">The exact tracked, untracked, and ignored worktree identity.</param>
    /// <param name="entries">The complete ordered stash reflog entries.</param>
    internal StashCatalog(
        RepositoryPrecondition precondition,
        RepositoryWorktreeFingerprint worktreeFingerprint,
        ImmutableArray<StashInfo> entries)
    {
        ArgumentNullException.ThrowIfNull(precondition);
        ArgumentNullException.ThrowIfNull(worktreeFingerprint);
        Precondition = precondition;
        WorktreeFingerprint = worktreeFingerprint;
        Entries = entries;
    }

    /// <summary>
    /// Gets the exact HEAD and index state captured with the catalog.
    /// </summary>
    internal RepositoryPrecondition Precondition { get; }

    /// <summary>
    /// Gets the exact tracked, untracked, and ignored worktree identity.
    /// </summary>
    internal RepositoryWorktreeFingerprint WorktreeFingerprint { get; }

    /// <summary>
    /// Gets the complete ordered stash reflog entries.
    /// </summary>
    internal ImmutableArray<StashInfo> Entries { get; }

    /// <summary>
    /// Finds one entry by its generated selector index and exact object identifier.
    /// </summary>
    /// <param name="expected">The exact displayed stash entry.</param>
    /// <returns>The matching live entry, or <see langword="null"/> when absent or changed.</returns>
    internal StashInfo? FindMatching(StashInfo expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return expected.Index < Entries.Length && Entries[expected.Index].Matches(expected)
            ? Entries[expected.Index]
            : null;
    }

    /// <summary>
    /// Determines whether another catalog has the same precondition, worktree, and reflog state.
    /// </summary>
    /// <param name="other">The independently captured live catalog.</param>
    /// <returns><see langword="true"/> when every action-relevant value matches exactly.</returns>
    internal bool Matches(StashCatalog other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Precondition.Matches(other.Precondition) &&
            WorktreeFingerprint.Matches(other.WorktreeFingerprint) &&
            EntriesMatch(other);
    }

    /// <summary>
    /// Determines whether another catalog has the same complete ordered stash reflog only.
    /// </summary>
    /// <param name="other">The independently captured live catalog.</param>
    /// <returns><see langword="true"/> when every stash entry matches exactly.</returns>
    internal bool EntriesMatch(StashCatalog other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Entries.Length == other.Entries.Length &&
            Entries.Zip(other.Entries).All(pair => pair.First.Matches(pair.Second));
    }
}
