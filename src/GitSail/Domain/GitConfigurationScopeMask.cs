namespace GitSail.Domain;

/// <summary>
/// Identifies the configuration scopes to which one registered value may be written.
/// </summary>
[Flags]
internal enum GitConfigurationScopeMask
{
    /// <summary>
    /// Identifies no writable configuration scope.
    /// </summary>
    None = 0,

    /// <summary>
    /// Identifies the user-global configuration scope.
    /// </summary>
    Global = 1,

    /// <summary>
    /// Identifies the repository-local configuration scope.
    /// </summary>
    Local = 2,

    /// <summary>
    /// Identifies the linked-worktree-specific configuration scope.
    /// </summary>
    Worktree = 4,

    /// <summary>
    /// Identifies every user-writable scope exposed by GitSail options.
    /// </summary>
    UserWritable = Global | Local | Worktree,
}
