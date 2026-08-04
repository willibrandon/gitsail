namespace GitSail.Domain;

/// <summary>
/// Identifies one present base, ours, or theirs index stage by canonical mode and object ID.
/// </summary>
/// <param name="Mode">The canonical Git index entry mode.</param>
/// <param name="ObjectId">The exact blob or gitlink object recorded at this stage.</param>
internal sealed record ConflictStage(
    GitFileMode Mode,
    ObjectId ObjectId);
