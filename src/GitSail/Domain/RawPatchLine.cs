namespace GitSail.Domain;

/// <summary>
/// Indexes one exact prefixed line inside a unified patch hunk.
/// </summary>
/// <param name="Offset">The byte offset relative to the file patch.</param>
/// <param name="Length">The exact byte length including any line terminator.</param>
/// <param name="LineNumber">The one-based presentation line number.</param>
/// <param name="Kind">The patch-line semantic kind.</param>
internal readonly record struct RawPatchLine(
    int Offset,
    int Length,
    int LineNumber,
    RawPatchLineKind Kind);
