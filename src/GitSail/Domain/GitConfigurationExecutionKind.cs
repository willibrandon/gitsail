namespace GitSail.Domain;

/// <summary>
/// Identifies how a configuration value can select or supply executable behavior.
/// </summary>
internal enum GitConfigurationExecutionKind
{
    /// <summary>
    /// Identifies configuration that cannot select executable behavior.
    /// </summary>
    None,

    /// <summary>
    /// Identifies a configured hooks directory.
    /// </summary>
    Hooks,

    /// <summary>
    /// Identifies an external diff or text-conversion command.
    /// </summary>
    Diff,

    /// <summary>
    /// Identifies a clean, smudge, or long-running process filter.
    /// </summary>
    Filter,

    /// <summary>
    /// Identifies a configured Git GUI or merge tool.
    /// </summary>
    Tool,

    /// <summary>
    /// Identifies a configured editor or sequence editor.
    /// </summary>
    Editor,

    /// <summary>
    /// Identifies a configured browser command or executable path.
    /// </summary>
    Browser,

    /// <summary>
    /// Identifies a configured credential helper.
    /// </summary>
    CredentialHelper,

    /// <summary>
    /// Identifies a configured SSH command.
    /// </summary>
    Ssh,

    /// <summary>
    /// Identifies a remote transport command or helper-selecting value.
    /// </summary>
    Remote,

    /// <summary>
    /// Identifies a configured signing program.
    /// </summary>
    Signing,
}
