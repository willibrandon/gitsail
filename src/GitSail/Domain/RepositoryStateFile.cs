namespace GitSail.Domain;

/// <summary>
/// Identifies a design-allowlisted file whose location must be resolved by Git.
/// </summary>
internal enum RepositoryStateFile
{
    /// <summary>
    /// Identifies the recoverable commit-message draft.
    /// </summary>
    Message,

    /// <summary>
    /// Identifies the recoverable commit-message backup.
    /// </summary>
    MessageBackup,

    /// <summary>
    /// Identifies the draft supplied to the Git commit transaction.
    /// </summary>
    EditMessage,

    /// <summary>
    /// Identifies the prepare-commit-message hook lifecycle file.
    /// </summary>
    PrepareCommitMessage,

    /// <summary>
    /// Identifies Git's transaction message after a commit attempt.
    /// </summary>
    CommitEditMessage,

    /// <summary>
    /// Identifies Git's merge-created commit message.
    /// </summary>
    MergeMessage,

    /// <summary>
    /// Identifies Git's squash-created commit message.
    /// </summary>
    SquashMessage,

    /// <summary>
    /// Identifies the index lock for separately confirmed stale-lock recovery.
    /// </summary>
    IndexLock,
}
