namespace GitSail.Domain;

/// <summary>
/// Indexes one exact file patch within a raw diff byte spool.
/// </summary>
/// <param name="OldPath">The repository path on the left side of the comparison.</param>
/// <param name="NewPath">The repository path on the right side of the comparison.</param>
/// <param name="Offset">The starting byte offset of the file patch.</param>
/// <param name="Length">The exact byte length of the file patch.</param>
/// <param name="HasHunks">Whether the file patch contains at least one textual hunk.</param>
/// <param name="IsBinary">Whether Git emitted binary patch or binary-difference content.</param>
internal sealed record RawDiffFile(
    GitPath OldPath,
    GitPath NewPath,
    long Offset,
    long Length,
    bool HasHunks,
    bool IsBinary);
