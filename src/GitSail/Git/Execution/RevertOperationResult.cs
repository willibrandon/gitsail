using GitSail.Domain;

namespace GitSail.Git.Execution;

/// <summary>
/// Contains one successful worktree revert and the live repository precondition captured before it.
/// </summary>
/// <param name="Operation">The exact successful Git operation output and warnings.</param>
/// <param name="Precondition">The HEAD and staged-index identity that must match before undo.</param>
internal sealed record RevertOperationResult(
    GitOperationResult Operation,
    RepositoryPrecondition Precondition);
