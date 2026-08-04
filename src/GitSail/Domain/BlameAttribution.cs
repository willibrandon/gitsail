namespace GitSail.Domain;

/// <summary>
/// Associates one result line with its source line and exact Git origin metadata.
/// </summary>
/// <param name="ResultLineNumber">The one-based line number in the displayed file.</param>
/// <param name="SourceLineNumber">The one-based line number in the origin file.</param>
/// <param name="Commit">The shared exact commit metadata.</param>
/// <param name="SourcePath">The exact origin path reported by Git.</param>
/// <param name="Previous">The optional previous commit and path reported for this origin group.</param>
/// <param name="IsBoundary">Whether Git marked the origin as a history boundary.</param>
internal sealed record BlameAttribution(
    int ResultLineNumber,
    int SourceLineNumber,
    BlameCommit Commit,
    GitPath SourcePath,
    BlamePrevious? Previous,
    bool IsBoundary);
