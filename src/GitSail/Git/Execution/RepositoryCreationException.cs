namespace GitSail.Git.Execution;

/// <summary>
/// Reports an unsuccessful repository creation and any identity-checked cleanup offer.
/// </summary>
internal sealed class RepositoryCreationException : InvalidOperationException
{
    /// <summary>
    /// Initializes one unsuccessful repository creation result.
    /// </summary>
    /// <param name="exitCode">The Git process exit code.</param>
    /// <param name="message">The bounded Git failure explanation.</param>
    /// <param name="cleanup">The exact newly created directory eligible for cleanup, or <see langword="null"/>.</param>
    internal RepositoryCreationException(
        int exitCode,
        string message,
        CreatedDirectoryCleanup? cleanup)
        : base(message)
    {
        ExitCode = exitCode;
        Cleanup = cleanup;
    }

    /// <summary>
    /// Gets the Git process exit code.
    /// </summary>
    internal int ExitCode { get; }

    /// <summary>
    /// Gets the exact newly created directory eligible for identity-checked cleanup.
    /// </summary>
    internal CreatedDirectoryCleanup? Cleanup { get; }
}
