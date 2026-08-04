namespace GitSail.Domain;

/// <summary>
/// Contains the exact present base, ours, and theirs stages for one unmerged path.
/// </summary>
/// <param name="Base">Stage 1 from the merge base, or <see langword="null"/> when absent.</param>
/// <param name="Ours">Stage 2 from the current side, or <see langword="null"/> when absent.</param>
/// <param name="Theirs">Stage 3 from the incoming side, or <see langword="null"/> when absent.</param>
/// <param name="WorkTreeMode">The observed worktree mode, or <see langword="null"/> when absent.</param>
internal sealed record ConflictStages(
    ConflictStage? Base,
    ConflictStage? Ours,
    ConflictStage? Theirs,
    GitFileMode? WorkTreeMode);
