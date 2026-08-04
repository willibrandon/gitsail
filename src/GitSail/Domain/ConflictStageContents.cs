namespace GitSail.Domain;

/// <summary>
/// Contains exact optional base, ours, and theirs object content for one unmerged path.
/// </summary>
/// <param name="Base">Stage 1 content, or <see langword="null"/> when the stage is absent.</param>
/// <param name="Ours">Stage 2 content, or <see langword="null"/> when the stage is absent.</param>
/// <param name="Theirs">Stage 3 content, or <see langword="null"/> when the stage is absent.</param>
internal sealed record ConflictStageContents(
    ConflictStageContent? Base,
    ConflictStageContent? Ours,
    ConflictStageContent? Theirs);
