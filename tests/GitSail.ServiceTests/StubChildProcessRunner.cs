using GitSail.Git.Execution;

namespace GitSail.ServiceTests;

/// <summary>
/// Captures one typed child invocation and returns a deterministic asynchronous result.
/// </summary>
internal sealed class StubChildProcessRunner : IChildProcessRunner
{
    /// <summary>
    /// Gets or sets the result factory invoked for the captured process request.
    /// </summary>
    internal required Func<ProcessInvocation, CancellationToken, Task<ProcessResult>> Handler { get; init; }

    /// <summary>
    /// Gets the most recently captured invocation.
    /// </summary>
    internal ProcessInvocation? Invocation { get; private set; }

    /// <inheritdoc />
    public Task<ProcessResult> RunAsync(
        ProcessInvocation invocation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        Invocation = invocation;
        return Handler(invocation, cancellationToken);
    }
}
