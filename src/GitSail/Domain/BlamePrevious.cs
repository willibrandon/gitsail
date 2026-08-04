namespace GitSail.Domain;

/// <summary>
/// Identifies the prior commit and file path from which one blamed line was derived.
/// </summary>
/// <param name="ObjectId">The exact prior commit object identifier.</param>
/// <param name="Path">The exact prior repository-relative path.</param>
internal sealed record BlamePrevious(ObjectId ObjectId, GitPath Path);
