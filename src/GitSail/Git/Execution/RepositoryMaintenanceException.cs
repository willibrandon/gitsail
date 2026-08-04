namespace GitSail.Git.Execution;

/// <summary>
/// Represents a repository maintenance or verification command that completed unsuccessfully.
/// </summary>
internal sealed class RepositoryMaintenanceException : InvalidOperationException
{
    /// <summary>
    /// Initializes a repository maintenance failure with its exact bounded output.
    /// </summary>
    /// <param name="exitCode">The Git process exit code.</param>
    /// <param name="message">The concise failure explanation.</param>
    /// <param name="standardOutput">The exact bounded standard-output bytes.</param>
    /// <param name="standardError">The exact bounded standard-error bytes.</param>
    internal RepositoryMaintenanceException(
        int exitCode,
        string message,
        ReadOnlyMemory<byte> standardOutput,
        ReadOnlyMemory<byte> standardError)
        : base(message)
    {
        ExitCode = exitCode;
        StandardOutput = standardOutput;
        StandardError = standardError;
    }

    /// <summary>
    /// Gets the Git process exit code.
    /// </summary>
    internal int ExitCode { get; }

    /// <summary>
    /// Gets the exact bounded standard-output bytes.
    /// </summary>
    internal ReadOnlyMemory<byte> StandardOutput { get; }

    /// <summary>
    /// Gets the exact bounded standard-error bytes.
    /// </summary>
    internal ReadOnlyMemory<byte> StandardError { get; }
}
