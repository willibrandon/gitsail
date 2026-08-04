using GitSail.Git.Execution;

namespace GitSail.Domain;

/// <summary>
/// Contains the created canonical linked worktree and bounded Git output.
/// </summary>
/// <param name="Directory">The canonical created linked-worktree directory.</param>
/// <param name="Operation">The exact bounded standard output and error.</param>
internal sealed record WorktreeCreationResult(
    CanonicalDirectory Directory,
    GitOperationResult Operation);
