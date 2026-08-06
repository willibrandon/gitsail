namespace GitSail.Domain;

/// <summary>
/// Identifies the lifecycle state of one supervised operation.
/// </summary>
internal enum OperationState
{
    /// <summary>
    /// Indicates that the operation has been accepted and started.
    /// </summary>
    Started,

    /// <summary>
    /// Indicates that the operation published a progress update.
    /// </summary>
    Running,

    /// <summary>
    /// Indicates that the operation completed successfully.
    /// </summary>
    Completed,

    /// <summary>
    /// Indicates that cancellation stopped the operation.
    /// </summary>
    Canceled,

    /// <summary>
    /// Indicates that the operation failed with an observed exception.
    /// </summary>
    Failed,
}
