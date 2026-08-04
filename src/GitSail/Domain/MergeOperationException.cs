namespace GitSail.Domain;

/// <summary>
/// Represents an actionable merge request that cannot start in the current repository state.
/// </summary>
internal sealed class MergeOperationException : Exception
{
    /// <summary>
    /// Initializes an actionable merge request failure.
    /// </summary>
    /// <param name="message">The control-safe failure explanation.</param>
    internal MergeOperationException(string message)
        : base(message)
    {
    }
}
