namespace GitSail.Domain;

/// <summary>
/// Identifies the precedence scope reported for one Git configuration value.
/// </summary>
internal enum GitConfigurationScope
{
    /// <summary>
    /// Identifies a worktree-specific configuration value.
    /// </summary>
    Worktree,

    /// <summary>
    /// Identifies a repository-local configuration value.
    /// </summary>
    Local,

    /// <summary>
    /// Identifies a user-global configuration value.
    /// </summary>
    Global,

    /// <summary>
    /// Identifies a system configuration value.
    /// </summary>
    System,

    /// <summary>
    /// Identifies a command-scope configuration override.
    /// </summary>
    Command,

    /// <summary>
    /// Identifies a value for which Git reported no known scope.
    /// </summary>
    Unknown,
}
