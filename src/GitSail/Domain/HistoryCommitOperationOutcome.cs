namespace GitSail.Domain;

/// <summary>
/// Describes whether a history commit operation completed or stopped for user action.
/// </summary>
internal enum HistoryCommitOperationOutcome
{
    /// <summary>
    /// Indicates that Git completed the requested operation and cleared its operation state.
    /// </summary>
    Completed,

    /// <summary>
    /// Indicates that Git retained operation state for conflict resolution or another decision.
    /// </summary>
    Stopped,
}
