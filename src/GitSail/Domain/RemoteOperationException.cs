namespace GitSail.Domain;

/// <summary>
/// Represents an actionable remote transaction that cannot proceed from current exact state.
/// </summary>
internal sealed class RemoteOperationException : Exception
{
    /// <summary>
    /// Initializes an actionable remote transaction failure.
    /// </summary>
    /// <param name="message">The control-safe failure explanation.</param>
    internal RemoteOperationException(string message)
        : base(message)
    {
    }
}
