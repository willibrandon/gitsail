namespace GitSail.Domain;

/// <summary>
/// Identifies the repository resource contract held by a mutation lease.
/// </summary>
internal enum RepositoryMutationPurpose
{
    /// <summary>
    /// Identifies a read of index-dependent state.
    /// </summary>
    ReadIndex,

    /// <summary>
    /// Identifies an index refresh.
    /// </summary>
    RefreshIndex,

    /// <summary>
    /// Identifies an index content update.
    /// </summary>
    UpdateIndex,

    /// <summary>
    /// Identifies application of a generated patch.
    /// </summary>
    ApplyPatch,

    /// <summary>
    /// Identifies a checkout or switch operation.
    /// </summary>
    Checkout,

    /// <summary>
    /// Identifies a commit transaction.
    /// </summary>
    Commit,

    /// <summary>
    /// Identifies a merge transaction.
    /// </summary>
    Merge,

    /// <summary>
    /// Identifies an abort of repository transaction state.
    /// </summary>
    Abort,

    /// <summary>
    /// Identifies a rebase transaction.
    /// </summary>
    Rebase,

    /// <summary>
    /// Identifies a stash ref or worktree transaction.
    /// </summary>
    Stash,

    /// <summary>
    /// Identifies a remote operation that can update local or remote refs.
    /// </summary>
    RemoteMutation,
}
