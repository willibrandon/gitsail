namespace GitSail.Domain;

/// <summary>
/// Selects the minimum severity retained by structured application tracing.
/// </summary>
internal enum GitSailLogLevel
{
    /// <summary>
    /// Retains every diagnostic event.
    /// </summary>
    Trace,

    /// <summary>
    /// Retains detailed child-operation events and higher severities.
    /// </summary>
    Debug,

    /// <summary>
    /// Retains ordinary application lifecycle events and higher severities.
    /// </summary>
    Information,

    /// <summary>
    /// Retains warning, error, and critical events.
    /// </summary>
    Warning,

    /// <summary>
    /// Retains error and critical events.
    /// </summary>
    Error,

    /// <summary>
    /// Retains only critical events.
    /// </summary>
    Critical,

    /// <summary>
    /// Disables all subsequent structured trace events.
    /// </summary>
    None,
}
