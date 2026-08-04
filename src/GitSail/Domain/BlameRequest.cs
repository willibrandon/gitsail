namespace GitSail.Domain;

/// <summary>
/// Describes one exact file, revision, range, and origin-detection request for Git blame.
/// </summary>
/// <param name="Revision">The optional immutable commit revision, or <see langword="null"/> for worktree bytes.</param>
/// <param name="Path">The exact repository-relative file path.</param>
/// <param name="Range">The optional inclusive result-line range.</param>
/// <param name="DetectMoves">Whether Git should detect moved lines within the file.</param>
/// <param name="DetectCopies">Whether Git should detect lines copied from other files.</param>
internal sealed record BlameRequest(
    Revision? Revision,
    GitPath Path,
    BlameRange? Range,
    bool DetectMoves,
    bool DetectCopies);
