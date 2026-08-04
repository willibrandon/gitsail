namespace GitSail.Domain;

/// <summary>
/// Binds a validated revision expression to the exact commit object Git resolved.
/// </summary>
/// <param name="Revision">The original typed revision expression.</param>
/// <param name="CommitObjectId">The exact resolved commit object identifier.</param>
internal sealed record ResolvedRevision(
    Revision Revision,
    ObjectId CommitObjectId);
