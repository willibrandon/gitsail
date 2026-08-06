using GitSail.Git.Execution;

namespace GitSail.ServiceTests;

/// <summary>
/// Captures one terminal-attached invocation without starting an external process.
/// </summary>
internal sealed class RecordingTerminalChildProcessRunner : ITerminalChildProcessRunner
{
    /// <summary>
    /// Gets or sets the deterministic exit status returned by the fake runner.
    /// </summary>
    internal int ExitCode { get; set; }

    /// <summary>
    /// Gets the most recent complete invocation supplied to the fake runner.
    /// </summary>
    internal ProcessInvocation? Invocation { get; private set; }

    /// <inheritdoc />
    public Task<int> RunAsync(
        ProcessInvocation invocation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        cancellationToken.ThrowIfCancellationRequested();
        Invocation = invocation;
        return Task.FromResult(ExitCode);
    }
}
