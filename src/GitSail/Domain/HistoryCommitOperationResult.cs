namespace GitSail.Domain;

/// <summary>
/// Contains the outcome, exact Git output, and any operation state left for user action.
/// </summary>
/// <param name="Outcome">Whether the operation completed or stopped.</param>
/// <param name="StandardOutput">The exact bounded standard-output bytes from Git.</param>
/// <param name="StandardError">The exact bounded standard-error bytes from Git.</param>
/// <param name="State">The current retained operation state, or <see langword="null"/> after completion.</param>
internal sealed record HistoryCommitOperationResult(
    HistoryCommitOperationOutcome Outcome,
    ReadOnlyMemory<byte> StandardOutput,
    ReadOnlyMemory<byte> StandardError,
    HistoryCommitOperationState? State);
