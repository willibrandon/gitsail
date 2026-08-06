namespace GitSail.Domain;

/// <summary>
/// Identifies the completed disposition of one configured-tool request.
/// </summary>
internal enum ConfiguredToolOutcome
{
    /// <summary>
    /// Identifies a request denied during executable capability review.
    /// </summary>
    Denied,

    /// <summary>
    /// Identifies a configured command that exited successfully.
    /// </summary>
    Succeeded,

    /// <summary>
    /// Identifies a configured command that returned a nonzero exit status.
    /// </summary>
    Failed,
}
