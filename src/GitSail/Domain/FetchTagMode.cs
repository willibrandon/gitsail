namespace GitSail.Domain;

/// <summary>
/// Selects configured, all-tag, or no-tag behavior for one fetch transaction.
/// </summary>
internal enum FetchTagMode
{
    /// <summary>
    /// Honors the remote and global Git tag configuration.
    /// </summary>
    Configured,

    /// <summary>
    /// Fetches every tag from the selected remote or remotes.
    /// </summary>
    All,

    /// <summary>
    /// Disables automatic tag following for the transaction.
    /// </summary>
    None,
}
