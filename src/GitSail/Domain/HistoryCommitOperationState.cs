namespace GitSail.Domain;

/// <summary>
/// Identifies one exact cherry-pick or commit-revert operation retained by Git.
/// </summary>
/// <param name="Operation">The Git operation that is waiting for user action.</param>
/// <param name="Commit">The exact commit currently being applied or reverted.</param>
internal sealed record HistoryCommitOperationState(
    HistoryCommitOperation Operation,
    ObjectId Commit);
