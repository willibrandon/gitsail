namespace GitSail;

/// <summary>
/// Defines the process exit codes in the command-line contract.
/// </summary>
internal static class ExitCodes
{
    /// <summary>
    /// Indicates successful completion.
    /// </summary>
    internal const int Success = 0;

    /// <summary>
    /// Indicates a repository, Git, or operation failure.
    /// </summary>
    internal const int Failure = 1;

    /// <summary>
    /// Indicates invalid command-line usage.
    /// </summary>
    internal const int Usage = 2;

    /// <summary>
    /// Indicates cancellation by the user.
    /// </summary>
    internal const int Cancelled = 130;
}
