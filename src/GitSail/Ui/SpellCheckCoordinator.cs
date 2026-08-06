using GitSail.Domain;

namespace GitSail.Ui;

/// <summary>
/// Debounces, cancels, version-checks, and visibly degrades optional spelling work.
/// </summary>
internal sealed class SpellCheckCoordinator : IDisposable
{
    private readonly OperationSupervisor _operations;
    private readonly SpellingState _state;
    private readonly Func<long> _getCurrentDocumentVersion;
    private readonly Func<string, long, string, CancellationToken, Task<SpellCheckResult>> _checkAsync;
    private readonly Action _changed;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _delay;
    private readonly Lock _gate = new();
    private OperationId? _activeOperation;
    private long _requestSequence;
    private bool _enabled = true;
    private bool _disposed;

    /// <summary>
    /// Initializes an owned live-check coordinator over explicit time and process callbacks.
    /// </summary>
    /// <param name="operations">The repository background-operation owner.</param>
    /// <param name="state">The controlled spelling presentation state.</param>
    /// <param name="getCurrentDocumentVersion">Returns the editor version at result publication time.</param>
    /// <param name="checkAsync">Runs one bounded check over an exact message version.</param>
    /// <param name="changed">Requests a new application render after visible state changes.</param>
    /// <param name="timeProvider">The clock used for deterministic debounce delays.</param>
    /// <param name="delay">The idle delay before a live check begins.</param>
    internal SpellCheckCoordinator(
        OperationSupervisor operations,
        SpellingState state,
        Func<long> getCurrentDocumentVersion,
        Func<string, long, string, CancellationToken, Task<SpellCheckResult>> checkAsync,
        Action changed,
        TimeProvider timeProvider,
        TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(getCurrentDocumentVersion);
        ArgumentNullException.ThrowIfNull(checkAsync);
        ArgumentNullException.ThrowIfNull(changed);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay));
        }

        _operations = operations;
        _state = state;
        _getCurrentDocumentVersion = getCurrentDocumentVersion;
        _checkAsync = checkAsync;
        _changed = changed;
        _timeProvider = timeProvider;
        _delay = delay;
    }

    /// <summary>
    /// Replaces any pending live check with the newest complete editor snapshot.
    /// </summary>
    /// <param name="message">The complete commit-message text.</param>
    /// <param name="documentVersion">The exact captured editor version.</param>
    /// <param name="dictionary">The effective configured dictionary name.</param>
    internal void Schedule(string message, long documentVersion, string dictionary)
        => Start(message, documentVersion, dictionary, _delay, enable: false);

    /// <summary>
    /// Immediately retries checking even after an earlier optional-feature failure.
    /// </summary>
    /// <param name="message">The complete commit-message text.</param>
    /// <param name="documentVersion">The exact captured editor version.</param>
    /// <param name="dictionary">The effective configured dictionary name.</param>
    internal void CheckNow(string message, long documentVersion, string dictionary)
        => Start(message, documentVersion, dictionary, TimeSpan.Zero, enable: true);

    /// <summary>
    /// Cancels pending spelling work and prevents further scheduling.
    /// </summary>
    public void Dispose()
    {
        OperationId? operation;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _requestSequence++;
            operation = _activeOperation;
            _activeOperation = null;
        }

        if (operation is { } id)
        {
            _operations.Cancel(id);
        }
    }

    private void Start(
        string message,
        long documentVersion,
        string dictionary,
        TimeSpan delay,
        bool enable)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(dictionary);
        OperationId? previous;
        long requestSequence;
        lock (_gate)
        {
            if (_disposed || (!enable && !_enabled))
            {
                return;
            }

            if (enable)
            {
                _enabled = true;
            }

            previous = _activeOperation;
            _activeOperation = null;
            requestSequence = ++_requestSequence;
        }

        if (previous is { } previousId)
        {
            _operations.Cancel(previousId);
        }

        _state.BeginCheck(documentVersion);
        _changed();
        var operation = _operations.Start(
            "Checking commit spelling",
            context => RunAsync(
                message,
                documentVersion,
                dictionary,
                delay,
                requestSequence,
                context),
            CancellationToken.None);
        lock (_gate)
        {
            if (_disposed)
            {
                _operations.Cancel(operation);
            }
            else
            {
                _activeOperation = operation;
            }
        }
    }

    private async Task RunAsync(
        string message,
        long documentVersion,
        string dictionary,
        TimeSpan delay,
        long requestSequence,
        OperationContext context)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _timeProvider, context.CancellationToken).ConfigureAwait(false);
            }

            var result = await _checkAsync(
                message,
                documentVersion,
                dictionary,
                context.CancellationToken).ConfigureAwait(false);
            if (IsCurrentRequest(requestSequence) &&
                _getCurrentDocumentVersion() == documentVersion &&
                _state.TryComplete(result))
            {
                _changed();
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            var reason = TerminalTextSanitizer.Sanitize(exception.Message);
            if (reason.Length == 0)
            {
                reason = "The checker failed without an explanation.";
            }
            else if (reason.Length > 512)
            {
                var length = char.IsHighSurrogate(reason[508]) ? 508 : 509;
                reason = $"{reason[..length]}...";
            }

            if (IsCurrentRequest(requestSequence) &&
                _getCurrentDocumentVersion() == documentVersion &&
                _state.TryDisable(documentVersion, reason))
            {
                lock (_gate)
                {
                    if (requestSequence == _requestSequence)
                    {
                        _enabled = false;
                    }
                }

                _changed();
            }
        }
        finally
        {
            lock (_gate)
            {
                if (_activeOperation == context.Id)
                {
                    _activeOperation = null;
                }
            }
        }
    }

    private bool IsCurrentRequest(long requestSequence)
    {
        lock (_gate)
        {
            return !_disposed && requestSequence == _requestSequence;
        }
    }
}
