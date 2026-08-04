using GitSail.Git.Execution;
using System.Text;

namespace GitSail.ServiceTests;

/// <summary>
/// Runs a real clone to completion and then reports one deterministic failure for cleanup tests.
/// </summary>
internal sealed class SuccessfulCloneFailingProcessRunner : IChildProcessRunner
{
    private readonly IChildProcessRunner _inner;
    private int _failureRemaining = 1;

    /// <summary>
    /// Initializes post-clone failure injection over one real child-process runner.
    /// </summary>
    /// <param name="inner">The real process runner used to create the clone.</param>
    internal SuccessfulCloneFailingProcessRunner(IChildProcessRunner inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <summary>
    /// Delegates the command and replaces the first successful clone result with a failure result.
    /// </summary>
    /// <param name="invocation">The complete typed process invocation.</param>
    /// <param name="cancellationToken">Signals delegated process cancellation.</param>
    /// <returns>The delegated result or deterministic post-clone failure.</returns>
    public async Task<ProcessResult> RunAsync(
        ProcessInvocation invocation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var result = await _inner.RunAsync(invocation, cancellationToken);
        if (result.ExitCode == 0 &&
            invocation.Arguments.Any(static argument => argument.IsLiteral("clone")) &&
            Interlocked.Exchange(ref _failureRemaining, 0) == 1)
        {
            return result with
            {
                ExitCode = 1,
                StandardError = Encoding.UTF8.GetBytes("injected failure after clone creation"),
            };
        }

        return result;
    }
}
