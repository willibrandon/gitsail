namespace GitSail.Domain;

/// <summary>
/// Reports an unavailable, failed, or incompatible optional spell checker.
/// </summary>
internal sealed class SpellCheckException : Exception
{
    /// <summary>
    /// Initializes a spell-check failure with an actionable control-safe explanation.
    /// </summary>
    /// <param name="message">The actionable failure explanation.</param>
    internal SpellCheckException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a spell-check failure with its underlying execution failure.
    /// </summary>
    /// <param name="message">The actionable failure explanation.</param>
    /// <param name="innerException">The underlying execution or decoding failure.</param>
    internal SpellCheckException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
