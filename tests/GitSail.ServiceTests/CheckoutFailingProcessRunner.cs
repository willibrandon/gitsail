using GitSail.Git.Execution;
using System.Text;

namespace GitSail.ServiceTests;

/// <summary>
/// Delegates real Git commands while injecting one deterministic checkout-index failure.
/// </summary>
internal sealed class CheckoutFailingProcessRunner : IChildProcessRunner
{
    private readonly IChildProcessRunner _inner;
    private int _failureRemaining = 1;

    /// <summary>
    /// Initializes deterministic checkout failure injection over one real child-process runner.
    /// </summary>
    /// <param name="inner">The real process runner used for every non-injected invocation.</param>
    internal CheckoutFailingProcessRunner(IChildProcessRunner inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <summary>
    /// Fails the first checkout-index invocation and delegates every other typed command unchanged.
    /// </summary>
    /// <param name="invocation">The complete typed process invocation.</param>
    /// <param name="cancellationToken">Signals delegated process cancellation.</param>
    /// <returns>The injected failure or delegated process result.</returns>
    public Task<ProcessResult> RunAsync(
        ProcessInvocation invocation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (invocation.Arguments.Any(static argument => argument.IsLiteral("checkout-index")) &&
            Interlocked.Exchange(ref _failureRemaining, 0) == 1)
        {
            return Task.FromResult(new ProcessResult(
                ExitCode: 1,
                StandardOutput: ReadOnlyMemory<byte>.Empty,
                StandardError: Encoding.UTF8.GetBytes("injected checkout failure"),
                Duration: TimeSpan.Zero));
        }

        return _inner.RunAsync(invocation, cancellationToken);
    }
}
