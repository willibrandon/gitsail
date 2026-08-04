namespace GitSail.Domain;

/// <summary>
/// Reports a safe remote-initialization validation, capability, or execution failure.
/// </summary>
internal sealed class RemoteInitializationException : Exception
{
    /// <summary>
    /// Initializes a remote-initialization failure with an actionable control-safe message.
    /// </summary>
    /// <param name="message">The failure message.</param>
    internal RemoteInitializationException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a remote-initialization failure with its native or process cause.
    /// </summary>
    /// <param name="message">The failure message.</param>
    /// <param name="innerException">The underlying failure.</param>
    internal RemoteInitializationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
