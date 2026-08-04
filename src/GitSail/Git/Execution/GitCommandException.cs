namespace GitSail.Git.Execution;

/// <summary>
/// Represents a typed Git invocation that completed unsuccessfully.
/// </summary>
internal sealed class GitCommandException : InvalidOperationException
{
    /// <summary>
    /// Initializes a Git command failure with its exit code and sanitized explanation.
    /// </summary>
    /// <param name="exitCode">The Git process exit code.</param>
    /// <param name="message">The sanitized failure explanation.</param>
    internal GitCommandException(int exitCode, string message)
        : base(message)
    {
        ExitCode = exitCode;
    }

    /// <summary>
    /// Gets the Git process exit code.
    /// </summary>
    internal int ExitCode { get; }
}
