using System.Collections.Immutable;

namespace GitSail.Domain;

/// <summary>
/// Captures the exact repository state and dry-run output reviewed before pruning.
/// </summary>
/// <param name="Precondition">The repository precondition captured with the preview.</param>
/// <param name="StandardOutput">The exact bounded dry-run standard output.</param>
/// <param name="StandardError">The exact bounded dry-run standard error.</param>
internal sealed record WorktreePrunePlan(
    RepositoryPrecondition Precondition,
    ImmutableArray<byte> StandardOutput,
    ImmutableArray<byte> StandardError);
