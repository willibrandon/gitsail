using GitSail.Git.Execution;
using GitSail.Domain;
using System.Collections.Immutable;

namespace GitSail.Diagnostics;

/// <summary>
/// Routes secret-free diagnostics through the trace attached to the current asynchronous invocation.
/// </summary>
internal static class ApplicationTrace
{
    private static readonly AsyncLocal<TraceSession?> s_current = new();

    /// <summary>
    /// Gets whether the current application invocation has trace capture enabled.
    /// </summary>
    internal static bool IsEnabled => s_current.Value is not null;

    /// <summary>
    /// Gets the active trace path for display inside the application.
    /// </summary>
    internal static string? FilePath => s_current.Value?.FilePath;

    /// <summary>
    /// Activates one trace for the current asynchronous application invocation.
    /// </summary>
    /// <param name="session">The trace session to activate.</param>
    /// <returns>A scope that restores the prior trace when disposed.</returns>
    internal static ApplicationTraceScope Begin(TraceSession session)
        => new(session);

    /// <summary>
    /// Gets a stable snapshot of sanitized entries for the in-application drawer.
    /// </summary>
    /// <returns>The retained display entries in event order.</returns>
    internal static ImmutableArray<TraceDisplayEntry> GetDisplayEntries()
        => s_current.Value?.GetDisplayEntries() ?? [];

    /// <summary>
    /// Applies the latest configured minimum severity to the active trace.
    /// </summary>
    /// <param name="minimumLevel">The minimum severity retained after this call.</param>
    internal static void SetMinimumLevel(GitSailLogLevel minimumLevel)
        => s_current.Value?.SetMinimumLevel(minimumLevel);

    /// <summary>
    /// Records one child-process start without arguments, environment values, input, or output content.
    /// </summary>
    /// <param name="invocation">The typed child invocation.</param>
    /// <param name="terminalAttached">Whether the child inherits terminal streams.</param>
    /// <returns>The trace-local child operation identifier, or zero when tracing is disabled.</returns>
    internal static long ChildStarted(ProcessInvocation invocation, bool terminalAttached)
        => s_current.Value?.WriteChildStarted(invocation, terminalAttached) ?? 0;

    /// <summary>
    /// Records one completed redirected child process.
    /// </summary>
    /// <param name="operationId">The trace-local child operation identifier.</param>
    /// <param name="result">The bounded child result.</param>
    internal static void ChildCompleted(long operationId, ProcessResult result)
        => s_current.Value?.WriteChildCompleted(operationId, result);

    /// <summary>
    /// Records one completed terminal-attached child process.
    /// </summary>
    /// <param name="operationId">The trace-local child operation identifier.</param>
    /// <param name="exitCode">The normalized child exit status.</param>
    /// <param name="duration">The elapsed child duration.</param>
    internal static void TerminalChildCompleted(long operationId, int exitCode, TimeSpan duration)
        => s_current.Value?.WriteTerminalChildCompleted(operationId, exitCode, duration);

    /// <summary>
    /// Records a child-process failure using only the exception type and cancellation state.
    /// </summary>
    /// <param name="operationId">The trace-local child operation identifier.</param>
    /// <param name="exception">The child-boundary exception.</param>
    /// <param name="duration">The elapsed time before failure.</param>
    internal static void ChildFailed(long operationId, Exception exception, TimeSpan duration)
        => s_current.Value?.WriteChildFailed(operationId, exception, duration);

    /// <summary>
    /// Replaces the current asynchronous trace and returns the previous value.
    /// </summary>
    /// <param name="session">The replacement trace, or <see langword="null"/>.</param>
    /// <returns>The previously active trace.</returns>
    internal static TraceSession? Exchange(TraceSession? session)
    {
        var previous = s_current.Value;
        s_current.Value = session;
        return previous;
    }
}
