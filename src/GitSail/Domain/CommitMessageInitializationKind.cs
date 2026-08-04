namespace GitSail.Domain;

/// <summary>
/// Identifies the source selected for the initial commit-editor message.
/// </summary>
internal enum CommitMessageInitializationKind
{
    /// <summary>
    /// Indicates that no existing message supplied initial editor content.
    /// </summary>
    Empty,

    /// <summary>
    /// Indicates that a GitSail recovery file supplied the message.
    /// </summary>
    Recovery,

    /// <summary>
    /// Indicates that Git's pending merge state supplied the message.
    /// </summary>
    Merge,

    /// <summary>
    /// Indicates that Git's pending squash state supplied the message.
    /// </summary>
    Squash,

    /// <summary>
    /// Indicates that the exact commit selected for amend supplied the message.
    /// </summary>
    Amend,
}
