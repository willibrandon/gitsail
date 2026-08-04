namespace GitSail.Domain;

/// <summary>
/// Identifies the semantic prefix of one exact line inside a unified patch hunk.
/// </summary>
internal enum RawPatchLineKind
{
    /// <summary>
    /// Represents a context line present on both sides.
    /// </summary>
    Context,

    /// <summary>
    /// Represents a line added on the new side.
    /// </summary>
    Addition,

    /// <summary>
    /// Represents a line removed from the old side.
    /// </summary>
    Deletion,

    /// <summary>
    /// Represents Git's no-final-newline marker.
    /// </summary>
    NoNewlineMarker,
}
