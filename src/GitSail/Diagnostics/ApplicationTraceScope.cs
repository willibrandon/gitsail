namespace GitSail.Diagnostics;

/// <summary>
/// Restores the previous asynchronous application trace when one invocation scope ends.
/// </summary>
internal sealed class ApplicationTraceScope : IDisposable
{
    private readonly TraceSession? _previous;
    private TraceSession? _current;

    /// <summary>
    /// Activates one trace session and retains the prior asynchronous value.
    /// </summary>
    /// <param name="session">The trace session activated for this invocation.</param>
    internal ApplicationTraceScope(TraceSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _previous = ApplicationTrace.Exchange(session);
        _current = session;
    }

    /// <summary>
    /// Restores the trace that was active before this scope.
    /// </summary>
    public void Dispose()
    {
        if (_current is null)
        {
            return;
        }

        _ = ApplicationTrace.Exchange(_previous);
        _current = null;
    }
}
