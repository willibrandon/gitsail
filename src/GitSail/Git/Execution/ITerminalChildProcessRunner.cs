namespace GitSail.Git.Execution;

/// <summary>
/// Runs a typed child process with the parent terminal streams attached.
/// </summary>
internal interface ITerminalChildProcessRunner
{
    /// <summary>
    /// Runs one terminal-attached invocation and returns its normalized exit status.
    /// </summary>
    /// <param name="invocation">The complete child-process contract.</param>
    /// <param name="cancellationToken">Signals cancellation and child-tree termination.</param>
    /// <returns>The normalized child exit status.</returns>
    Task<int> RunAsync(ProcessInvocation invocation, CancellationToken cancellationToken);
}
