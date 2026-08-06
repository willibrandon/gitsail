using GitSail.Git.Execution;

namespace GitSail.Ui;

/// <summary>
/// Serializes executable-capability reviews into controlled nonpersistent UI state.
/// </summary>
internal sealed class ExecutableCapabilityCoordinator : IExecutableCapabilityResponder, IDisposable
{
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _queue = new(1, 1);
    private TaskCompletionSource<ExecutableCapabilityDecision>? _response;
    private ExecutableCapabilityPrompt? _current;
    private long _nextRequestId;
    private volatile bool _disposed;

    /// <summary>
    /// Notifies the workspace that the pending executable review changed.
    /// </summary>
    internal event Action? Changed;

    /// <summary>
    /// Gets the currently queued executable review, or no prompt while none is waiting.
    /// </summary>
    internal ExecutableCapabilityPrompt? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>
    /// Queues one exact executable review and waits for an explicit controlled decision.
    /// </summary>
    /// <param name="request">The exact command and data-exposure review.</param>
    /// <param name="cancellationToken">Signals review cancellation.</param>
    /// <returns>The explicit decision selected for this exact request.</returns>
    public async Task<ExecutableCapabilityDecision> RequestAsync(
        ExecutableCapabilityRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _queue.WaitAsync(cancellationToken).ConfigureAwait(false);
        TaskCompletionSource<ExecutableCapabilityDecision> completion;
        try
        {
            completion = new TaskCompletionSource<ExecutableCapabilityDecision>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _current = new ExecutableCapabilityPrompt(
                    Interlocked.Increment(ref _nextRequestId),
                    request);
                _response = completion;
            }

            Changed?.Invoke();
            using var cancellationRegistration = cancellationToken.Register(
                static state => ((TaskCompletionSource<ExecutableCapabilityDecision>)state!).TrySetCanceled(),
                completion);
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _current = null;
                _response = null;
            }

            Changed?.Invoke();
            _queue.Release();
        }
    }

    /// <summary>
    /// Submits one explicit decision for the exact currently displayed review.
    /// </summary>
    /// <param name="requestId">The displayed prompt identity.</param>
    /// <param name="decision">The explicit deny, one-time, or repository decision.</param>
    /// <returns><see langword="true"/> when the exact pending review accepted the decision.</returns>
    internal bool Decide(long requestId, ExecutableCapabilityDecision decision)
    {
        if (!Enum.IsDefined(decision))
        {
            throw new ArgumentOutOfRangeException(nameof(decision));
        }

        TaskCompletionSource<ExecutableCapabilityDecision>? completion;
        lock (_gate)
        {
            completion = _current?.Id == requestId ? _response : null;
        }

        return completion?.TrySetResult(decision) == true;
    }

    /// <summary>
    /// Denies the exact currently displayed review without persisting any state.
    /// </summary>
    /// <param name="requestId">The displayed prompt identity.</param>
    /// <returns><see langword="true"/> when the exact pending review was denied.</returns>
    internal bool Cancel(long requestId)
        => Decide(requestId, ExecutableCapabilityDecision.Deny);

    /// <summary>
    /// Denies any pending review and releases synchronization resources.
    /// </summary>
    public void Dispose()
    {
        TaskCompletionSource<ExecutableCapabilityDecision>? completion;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            completion = _response;
        }

        _ = completion?.TrySetResult(ExecutableCapabilityDecision.Deny);
    }
}
