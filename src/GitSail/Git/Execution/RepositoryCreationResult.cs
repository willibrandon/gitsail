namespace GitSail.Git.Execution;

/// <summary>
/// Contains a successful repository target and Git's exact bounded output.
/// </summary>
internal sealed class RepositoryCreationResult
{
    /// <summary>
    /// Initializes one successful repository creation result.
    /// </summary>
    /// <param name="directory">The canonical directory to open after creation.</param>
    /// <param name="operation">The exact successful Git output.</param>
    /// <param name="isBare">Whether the created repository has no worktree.</param>
    internal RepositoryCreationResult(
        CanonicalDirectory directory,
        GitOperationResult operation,
        bool isBare)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(operation);
        Directory = directory;
        Operation = operation;
        IsBare = isBare;
    }

    /// <summary>
    /// Gets the canonical directory to open after creation.
    /// </summary>
    internal CanonicalDirectory Directory { get; }

    /// <summary>
    /// Gets Git's exact bounded standard output and standard error.
    /// </summary>
    internal GitOperationResult Operation { get; }

    /// <summary>
    /// Gets whether the created repository has no worktree.
    /// </summary>
    internal bool IsBare { get; }
}
