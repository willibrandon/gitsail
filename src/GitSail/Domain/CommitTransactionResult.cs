namespace GitSail.Domain;

/// <summary>
/// Contains verified object identities and output from a completed commit transaction.
/// </summary>
/// <param name="PreviousHead">The precondition tip, or <see langword="null"/> for an unborn branch.</param>
/// <param name="NewHead">The exact commit created by Git.</param>
/// <param name="StandardOutput">The bounded exact standard-output bytes.</param>
/// <param name="StandardError">The bounded exact standard-error bytes.</param>
/// <param name="DraftCleanupWarning">A post-commit draft-cleanup warning, or <see langword="null"/>.</param>
internal sealed record CommitTransactionResult(
    ObjectId? PreviousHead,
    ObjectId NewHead,
    ReadOnlyMemory<byte> StandardOutput,
    ReadOnlyMemory<byte> StandardError,
    string? DraftCleanupWarning);
