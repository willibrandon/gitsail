using GitSail.Domain;

namespace GitSail.Ui;

/// <summary>
/// Retains one exact reverted patch and its observed HEAD identity for one-level worktree undo.
/// </summary>
internal sealed class RevertUndoState
{
    /// <summary>
    /// Initializes immutable ownership of one successfully reverted exact patch.
    /// </summary>
    /// <param name="patch">The exact forward patch capable of restoring the reverted bytes.</param>
    /// <param name="headObjectId">The observed HEAD identity when the revert succeeded.</param>
    internal RevertUndoState(byte[] patch, ObjectId? headObjectId)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (patch.Length == 0)
        {
            throw new ArgumentException("Revert undo requires a nonempty exact patch.", nameof(patch));
        }

        Patch = patch.ToArray();
        HeadObjectId = headObjectId;
    }

    /// <summary>
    /// Gets the exact forward patch retained outside repository metadata.
    /// </summary>
    internal ReadOnlyMemory<byte> Patch { get; }

    /// <summary>
    /// Gets the observed HEAD identity that must still match before undo begins.
    /// </summary>
    internal ObjectId? HeadObjectId { get; }
}
