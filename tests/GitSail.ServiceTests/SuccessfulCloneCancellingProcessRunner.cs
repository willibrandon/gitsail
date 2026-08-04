using GitSail.Git.Execution;

namespace GitSail.ServiceTests;

/// <summary>
/// Completes one real clone, cancels its operation token, and reports process cancellation.
/// </summary>
internal sealed class SuccessfulCloneCancellingProcessRunner : IChildProcessRunner
{
    private readonly IChildProcessRunner _inner;
    private readonly CancellationTokenSource _cancellation;

    /// <summary>
    /// Initializes the cancellation boundary around one real child-process runner.
    /// </summary>
    /// <param name="inner">The runner that creates the clone target.</param>
    /// <param name="cancellation">The operation cancellation source.</param>
    internal SuccessfulCloneCancellingProcessRunner(
        IChildProcessRunner inner,
        CancellationTokenSource cancellation)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(cancellation);
        _inner = inner;
        _cancellation = cancellation;
    }

    /// <summary>
    /// Runs Git to completion, then reproduces cancellation after target creation.
    /// </summary>
    /// <param name="invocation">The exact child-process invocation.</param>
    /// <param name="cancellationToken">The operation token cancelled after Git exits.</param>
    /// <returns>This method does not return a process result.</returns>
    public async Task<ProcessResult> RunAsync(
        ProcessInvocation invocation,
        CancellationToken cancellationToken)
    {
        _ = await _inner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        _cancellation.Cancel();
        throw new OperationCanceledException(cancellationToken);
    }
}
