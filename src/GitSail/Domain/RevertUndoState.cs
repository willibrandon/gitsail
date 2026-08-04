namespace GitSail.Domain;

/// <summary>
/// Retains one exact reverted patch, its live repository precondition, and recovery creation time.
/// </summary>
internal sealed class RevertUndoState
{
    private readonly byte[] _patch;

    /// <summary>
    /// Initializes immutable ownership of one successfully reverted exact patch.
    /// </summary>
    /// <param name="patch">The exact forward patch capable of restoring the reverted bytes.</param>
    /// <param name="precondition">The live HEAD object, attachment, and staged-index identity captured before revert.</param>
    /// <param name="createdAtUtc">The UTC time at which the revert recovery became eligible.</param>
    internal RevertUndoState(
        ReadOnlySpan<byte> patch,
        RepositoryPrecondition precondition,
        DateTimeOffset createdAtUtc)
    {
        if (patch.IsEmpty)
        {
            throw new ArgumentException("Revert undo requires a nonempty exact patch.", nameof(patch));
        }

        ArgumentNullException.ThrowIfNull(precondition);
        if (createdAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("A revert undo timestamp must use UTC.", nameof(createdAtUtc));
        }

        _patch = patch.ToArray();
        Precondition = precondition;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>
    /// Gets the exact forward patch retained outside repository metadata.
    /// </summary>
    internal ReadOnlyMemory<byte> Patch => _patch;

    /// <summary>
    /// Gets the live HEAD object, attachment, and staged-index identity required before undo.
    /// </summary>
    internal RepositoryPrecondition Precondition { get; }

    /// <summary>
    /// Gets the UTC creation time used to enforce bounded crash-recovery retention.
    /// </summary>
    internal DateTimeOffset CreatedAtUtc { get; }
}
