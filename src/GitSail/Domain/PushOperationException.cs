namespace GitSail.Domain;

/// <summary>
/// Reports a validated push-planning or execution condition suitable for direct presentation.
/// </summary>
internal sealed class PushOperationException : Exception
{
    /// <summary>
    /// Initializes a push-operation failure with a control-sanitized presentation message.
    /// </summary>
    /// <param name="message">The actionable push-operation message.</param>
    internal PushOperationException(string message)
        : base(message)
    {
    }
}
