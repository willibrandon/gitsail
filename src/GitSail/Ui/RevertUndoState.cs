using GitSail.Domain;

namespace GitSail.Ui;

/// <summary>
/// Retains one exact reverted patch and its live repository precondition for one-level worktree undo.
/// </summary>
internal sealed class RevertUndoState
{
    /// <summary>
    /// Initializes immutable ownership of one successfully reverted exact patch.
    /// </summary>
    /// <param name="patch">The exact forward patch capable of restoring the reverted bytes.</param>
    /// <param name="precondition">The live HEAD and staged-index identity captured before the revert.</param>
    internal RevertUndoState(byte[] patch, RepositoryPrecondition precondition)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(precondition);
        if (patch.Length == 0)
        {
            throw new ArgumentException("Revert undo requires a nonempty exact patch.", nameof(patch));
        }

        Patch = patch.ToArray();
        Precondition = precondition;
    }

    /// <summary>
    /// Gets the exact forward patch retained outside repository metadata.
    /// </summary>
    internal ReadOnlyMemory<byte> Patch { get; }

    /// <summary>
    /// Gets the live HEAD and staged-index identity that must still match before undo begins.
    /// </summary>
    internal RepositoryPrecondition Precondition { get; }
}
