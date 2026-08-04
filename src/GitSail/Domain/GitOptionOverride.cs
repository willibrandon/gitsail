namespace GitSail.Domain;

/// <summary>
/// Selects configured, explicitly enabled, or explicitly disabled Git option behavior.
/// </summary>
internal enum GitOptionOverride
{
    /// <summary>
    /// Omits the command option and honors effective Git configuration.
    /// </summary>
    Configured,

    /// <summary>
    /// Emits the positive command option explicitly.
    /// </summary>
    Enabled,

    /// <summary>
    /// Emits the negative command option explicitly.
    /// </summary>
    Disabled,
}
