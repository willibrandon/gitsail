namespace GitSail.Domain;

/// <summary>
/// Describes one immutable lifecycle update from a supervised operation.
/// </summary>
/// <param name="Sequence">The monotonically increasing supervisor update sequence.</param>
/// <param name="Id">The stable operation identifier.</param>
/// <param name="Name">The stable operation name.</param>
/// <param name="State">The lifecycle state represented by this update.</param>
/// <param name="Detail">The optional control-safe progress detail.</param>
/// <param name="Progress">The optional completion fraction from zero through one.</param>
/// <param name="Timestamp">The time at which the supervisor accepted the update.</param>
/// <param name="Failure">The observed failure for a failed operation.</param>
internal sealed record OperationSnapshot(
    long Sequence,
    OperationId Id,
    string Name,
    OperationState State,
    string? Detail,
    double? Progress,
    DateTimeOffset Timestamp,
    Exception? Failure);
