using System.Text;
using Hex1b;
using Hex1b.Widgets;

namespace GitSail.Ui;

/// <summary>
/// Owns one full-screen application and guarantees ordered terminal restoration.
/// </summary>
internal sealed class TerminalApplicationSession : IAsyncDisposable
{
    private static readonly ReadOnlyMemory<byte> s_exitBarrier = Encoding.ASCII.GetBytes(
        "\x1b[?2026l\x1b[0m\x1b[?2004l\x1b[?1006l\x1b[?1003l" +
        "\x1b[?1002l\x1b[?1000l\x1b[?25h\x1b[?1049l\x1b[0m");
    private static readonly TimeSpan BarrierTimeout = TimeSpan.FromSeconds(5);
    private readonly TerminalOutputBarrierPresentationAdapter _presentation;
    private readonly Hex1bAppWorkloadAdapter _workload;
    private int _runStarted;
    private bool _disposed;

    /// <summary>
    /// Initializes one application over an explicitly supplied terminal presentation.
    /// </summary>
    /// <param name="builder">The complete widget tree builder for the session.</param>
    /// <param name="options">The application rendering and input options.</param>
    /// <param name="presentation">The physical or test presentation receiving ordered output.</param>
    internal TerminalApplicationSession(
        Func<RootContext, Hex1bWidget> builder,
        Hex1bAppOptions options,
        IHex1bTerminalPresentationAdapter presentation)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(presentation);
        _presentation = new TerminalOutputBarrierPresentationAdapter(presentation);
        _workload = new Hex1bAppWorkloadAdapter(_presentation, maxQueuedOutputItems: 1)
        {
            EnableMouse = options.EnableMouse,
        };
        Terminal = new Hex1bTerminal(new Hex1bTerminalOptions
        {
            PresentationAdapter = _presentation,
            WorkloadAdapter = _workload,
        });
        options.WorkloadAdapter = _workload;
        Application = new Hex1bApp(builder, options);
    }

    /// <summary>
    /// Gets the application used by views for attachment and invalidation.
    /// </summary>
    internal Hex1bApp Application { get; }

    /// <summary>
    /// Gets the terminal that carries ordered application output and user input.
    /// </summary>
    internal Hex1bTerminal Terminal { get; }

    /// <summary>
    /// Creates a session connected to the current process console.
    /// </summary>
    /// <param name="builder">The complete widget tree builder for the session.</param>
    /// <param name="options">The application rendering and input options.</param>
    /// <returns>The ready console application session.</returns>
    internal static TerminalApplicationSession CreateConsole(
        Func<RootContext, Hex1bWidget> builder,
        Hex1bAppOptions options)
    {
        var session = new TerminalApplicationSession(
            builder,
            options,
            new ConsolePresentationAdapter(enableMouse: options.EnableMouse));
        WindowsConsoleInputMode.Apply();
        return session;
    }

    /// <summary>
    /// Clears the physical screen and emits the next application frame in full.
    /// </summary>
    internal void RequestCleanRepaint()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _workload.Write("\x1b[?2026h\x1b[0m\x1b[2J\x1b[H");
        _workload.RequestFullRepaint();
    }

    /// <summary>
    /// Runs until exit and waits for the final restoration sequence to reach the terminal.
    /// </summary>
    /// <param name="cancellationToken">Signals application shutdown.</param>
    /// <returns>A task that completes after the alternate screen is restored.</returns>
    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.Exchange(ref _runStarted, 1) != 0)
        {
            throw new InvalidOperationException("A terminal application session can only run once.");
        }

        try
        {
            await Application.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            using var barrierCancellation = new CancellationTokenSource(BarrierTimeout);
            try
            {
                await _presentation.WriteBarrierAsync(
                    _workload,
                    s_exitBarrier,
                    barrierCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (barrierCancellation.IsCancellationRequested)
            {
                // Disposal writes the same restoration modes directly if the terminal disconnected
                // before the ordered barrier could be acknowledged.
            }
        }
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await Terminal.DisposeAsync().ConfigureAwait(false);
        await Application.DisposeAsync().ConfigureAwait(false);
    }
}
