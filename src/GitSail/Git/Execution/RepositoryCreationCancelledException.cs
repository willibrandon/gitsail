namespace GitSail.Git.Execution;

/// <summary>
/// Reports a cancelled repository creation and any exact partial target eligible for cleanup.
/// </summary>
internal sealed class RepositoryCreationCancelledException : OperationCanceledException
{
    /// <summary>
    /// Initializes one cancelled repository creation result.
    /// </summary>
    /// <param name="cleanup">The exact newly created directory eligible for cleanup, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">The token that cancelled the Git process tree.</param>
    internal RepositoryCreationCancelledException(
        CreatedDirectoryCleanup? cleanup,
        CancellationToken cancellationToken)
        : base("Repository creation was cancelled.", cancellationToken)
    {
        Cleanup = cleanup;
    }

    /// <summary>
    /// Gets the exact partial target eligible for identity-checked cleanup.
    /// </summary>
    internal CreatedDirectoryCleanup? Cleanup { get; }
}
