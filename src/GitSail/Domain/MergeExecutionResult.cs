using GitSail.Git.Execution;

namespace GitSail.Domain;

/// <summary>
/// Contains a classified merge transition and Git's exact operation output.
/// </summary>
/// <param name="Outcome">The classified post-command repository state.</param>
/// <param name="Operation">The exact standard output and standard error bytes.</param>
/// <param name="HasMergeHead">Whether Git reports pending merge-parent state after execution.</param>
internal sealed record MergeExecutionResult(
    MergeOutcome Outcome,
    GitOperationResult Operation,
    bool HasMergeHead);
