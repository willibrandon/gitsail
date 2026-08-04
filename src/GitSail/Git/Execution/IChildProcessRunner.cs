namespace GitSail.Git.Execution;

/// <summary>
/// Runs the only allowed shell-free child-process boundary in GitSail.
/// </summary>
internal interface IChildProcessRunner
{
    /// <summary>
    /// Runs one typed invocation and captures its bounded byte streams independently.
    /// </summary>
    /// <param name="invocation">The complete child-process contract.</param>
    /// <param name="cancellationToken">Signals cancellation and child-tree termination.</param>
    /// <returns>The child exit status and exact retained output bytes.</returns>
    Task<ProcessResult> RunAsync(
        ProcessInvocation invocation,
        CancellationToken cancellationToken);
}
