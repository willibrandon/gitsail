using GitSail.Git.Execution;

namespace GitSail.Domain;

/// <summary>
/// Binds an exact selected remote and dry-run output to its complete displayed catalog.
/// </summary>
internal sealed class RemotePrunePlan
{
    /// <summary>
    /// Initializes one immutable remote-prune confirmation snapshot.
    /// </summary>
    /// <param name="catalog">The complete stable remote catalog shown to the user.</param>
    /// <param name="remote">The exact selected remote.</param>
    /// <param name="preview">Git's exact dry-run standard output and standard error.</param>
    internal RemotePrunePlan(
        RemoteCatalog catalog,
        RemoteInfo remote,
        GitOperationResult preview)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(preview);
        Catalog = catalog;
        Remote = remote;
        Preview = preview;
    }

    /// <summary>
    /// Gets the complete stable catalog bound to the confirmation.
    /// </summary>
    internal RemoteCatalog Catalog { get; }

    /// <summary>
    /// Gets the exact selected remote bound to the confirmation.
    /// </summary>
    internal RemoteInfo Remote { get; }

    /// <summary>
    /// Gets Git's exact dry-run output shown before pruning.
    /// </summary>
    internal GitOperationResult Preview { get; }
}
