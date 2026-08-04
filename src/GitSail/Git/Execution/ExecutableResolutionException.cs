namespace GitSail.Git.Execution;

/// <summary>
/// Represents a failure to locate a trusted executable on the sanitized search path.
/// </summary>
internal sealed class ExecutableResolutionException : InvalidOperationException
{
    /// <summary>
    /// Initializes an executable-resolution failure with an actionable message.
    /// </summary>
    /// <param name="message">The failure description.</param>
    internal ExecutableResolutionException(string message)
        : base(message)
    {
    }
}
