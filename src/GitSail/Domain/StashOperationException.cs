namespace GitSail.Domain;

/// <summary>
/// Reports an actionable validation failure in a typed stash workflow.
/// </summary>
internal sealed class StashOperationException : Exception
{
    /// <summary>
    /// Initializes one stash workflow failure with a control-safe caller-facing message.
    /// </summary>
    /// <param name="message">The actionable failure message.</param>
    internal StashOperationException(string message)
        : base(message)
    {
    }
}
