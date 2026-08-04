namespace GitSail.Domain;

/// <summary>
/// Describes one exact source update relative to one advertised remote destination.
/// </summary>
internal enum PushRelationship
{
    /// <summary>
    /// Creates a destination ref that is currently absent.
    /// </summary>
    New,

    /// <summary>
    /// Leaves a destination that already has the exact source object.
    /// </summary>
    UpToDate,

    /// <summary>
    /// Advances a destination through a proven fast-forward update.
    /// </summary>
    FastForward,

    /// <summary>
    /// Replaces a destination through a non-fast-forward update.
    /// </summary>
    NonFastForward,

    /// <summary>
    /// Deletes an existing destination ref.
    /// </summary>
    Delete,
}
