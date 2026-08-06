namespace GitSail.Domain;

/// <summary>
/// Classifies clipboard content before it reaches a terminal or child-process boundary.
/// </summary>
internal enum ClipboardContentClassification
{
    /// <summary>
    /// Identifies application-owned documentation and other public text.
    /// </summary>
    Public,

    /// <summary>
    /// Identifies repository paths, patches, messages, and other user repository data.
    /// </summary>
    RepositoryData,

    /// <summary>
    /// Identifies credentials and other values that must never reach the clipboard.
    /// </summary>
    Secret,
}
