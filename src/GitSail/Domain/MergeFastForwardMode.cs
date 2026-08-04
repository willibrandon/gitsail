namespace GitSail.Domain;

/// <summary>
/// Selects Git's fast-forward policy for one merge transaction.
/// </summary>
internal enum MergeFastForwardMode
{
    /// <summary>
    /// Honors Git's configured and target-specific default behavior.
    /// </summary>
    Default,

    /// <summary>
    /// Requires a fast-forward and rejects divergent history.
    /// </summary>
    FastForwardOnly,

    /// <summary>
    /// Creates a merge commit even when a fast-forward is possible.
    /// </summary>
    NoFastForward,
}
