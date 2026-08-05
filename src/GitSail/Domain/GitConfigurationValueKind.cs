namespace GitSail.Domain;

/// <summary>
/// Identifies the parser and validation contract for one registered configuration value.
/// </summary>
internal enum GitConfigurationValueKind
{
    /// <summary>
    /// Identifies an arbitrary non-NUL text value.
    /// </summary>
    String,

    /// <summary>
    /// Identifies an exact native path that may contain non-UTF-8 bytes on Unix.
    /// </summary>
    NativePath,

    /// <summary>
    /// Identifies a canonical Git boolean value.
    /// </summary>
    Boolean,

    /// <summary>
    /// Identifies a Git integer with optional binary-size suffix.
    /// </summary>
    Integer,

    /// <summary>
    /// Identifies a value selected from a registered set of names.
    /// </summary>
    Enumeration,

    /// <summary>
    /// Identifies a Git color and attribute expression.
    /// </summary>
    Color,

    /// <summary>
    /// Identifies the allowlisted additional diff-option sequence.
    /// </summary>
    DiffOptions,

    /// <summary>
    /// Identifies a collision-checked key-chord sequence.
    /// </summary>
    ChordList,

    /// <summary>
    /// Identifies a versioned layout record.
    /// </summary>
    Layout,

    /// <summary>
    /// Identifies a repository executable-capability grant record.
    /// </summary>
    Capability,
}
