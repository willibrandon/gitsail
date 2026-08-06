using Hex1b;
using Hex1b.Reflow;

namespace GitSail.Ui;

/// <summary>
/// Forwards terminal presentation operations and acknowledges one exact output barrier.
/// </summary>
internal sealed class TerminalOutputBarrierPresentationAdapter :
    IHex1bTerminalPresentationAdapter,
    ITerminalReflowProvider
{
    private static readonly ReadOnlyMemory<byte> s_cleanFrameRequest =
        "\x1b[0m\x1b[2J\x1b[H"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> s_synchronizedFrameBegin =
        "\x1b[?2026h"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> s_cleanScreenModes =
        "\x1b[?2026l\x1b[?7l\x1b[?25l\x1b[0m"u8.ToArray();
    private readonly IHex1bTerminalPresentationAdapter _inner;
    private readonly Action? _clearPhysicalScreen;
    private readonly Lock _gate = new();
    private ReadOnlyMemory<byte> _pendingBarrier;
    private TaskCompletionSource? _pendingCompletion;
    private int _clearBeforeNextFrame;

    /// <summary>
    /// Initializes a barrier-aware wrapper around the terminal's real presentation.
    /// </summary>
    /// <param name="inner">The presentation that owns the physical or test terminal.</param>
    /// <param name="clearPhysicalScreen">Clears a platform-owned screen buffer after synchronized output begins.</param>
    internal TerminalOutputBarrierPresentationAdapter(
        IHex1bTerminalPresentationAdapter inner,
        Action? clearPhysicalScreen = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _clearPhysicalScreen = clearPhysicalScreen;
    }

    int IHex1bTerminalPresentationAdapter.Width => _inner.Width;

    int IHex1bTerminalPresentationAdapter.Height => _inner.Height;

    TerminalCapabilities IHex1bTerminalPresentationAdapter.Capabilities => _inner.Capabilities;

    event Action<int, int>? IHex1bTerminalPresentationAdapter.Resized
    {
        add => _inner.Resized += value;
        remove => _inner.Resized -= value;
    }

    event Action? IHex1bTerminalPresentationAdapter.Disconnected
    {
        add => _inner.Disconnected += value;
        remove => _inner.Disconnected -= value;
    }

    bool ITerminalReflowProvider.ReflowEnabled
        => _inner is ITerminalReflowProvider { ReflowEnabled: true };

    bool ITerminalReflowProvider.ShouldClearSoftWrapOnAbsolutePosition
        => _inner is ITerminalReflowProvider
        {
            ShouldClearSoftWrapOnAbsolutePosition: true,
        };

    /// <summary>
    /// Enqueues one exact output barrier and waits until the presentation writes it.
    /// </summary>
    /// <param name="workload">The application workload whose output is drained in order.</param>
    /// <param name="barrier">The exact harmless terminal sequence used as the barrier.</param>
    /// <param name="cancellationToken">Bounds shutdown when the presentation has disconnected.</param>
    /// <returns>A task that completes only after all earlier output and the barrier are written.</returns>
    internal async Task WriteBarrierAsync(
        Hex1bAppWorkloadAdapter workload,
        ReadOnlyMemory<byte> barrier,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workload);
        if (barrier.IsEmpty)
        {
            throw new ArgumentException("The terminal output barrier cannot be empty.", nameof(barrier));
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_pendingCompletion is not null)
            {
                throw new InvalidOperationException("A terminal output barrier is already pending.");
            }

            _pendingBarrier = barrier;
            _pendingCompletion = completion;
        }

        try
        {
            workload.Write(barrier);
            await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pendingCompletion, completion))
                {
                    _pendingBarrier = default;
                    _pendingCompletion = null;
                }
            }
        }
    }

    async ValueTask IHex1bTerminalPresentationAdapter.WriteOutputAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        if (data.Span.SequenceEqual(s_cleanFrameRequest.Span))
        {
            Volatile.Write(ref _clearBeforeNextFrame, 1);
            return;
        }

        if (Volatile.Read(ref _clearBeforeNextFrame) != 0 &&
            data.Span.StartsWith(s_synchronizedFrameBegin.Span) &&
            Interlocked.Exchange(ref _clearBeforeNextFrame, 0) != 0)
        {
            await _inner.WriteOutputAsync(
                s_cleanScreenModes,
                cancellationToken).ConfigureAwait(false);
            TryClearPhysicalScreen();
            await _inner.WriteOutputAsync(
                CreateCleanScreenOverwrite(_inner.Width, _inner.Height),
                cancellationToken).ConfigureAwait(false);
            await _inner.WriteOutputAsync(data, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _inner.WriteOutputAsync(data, cancellationToken).ConfigureAwait(false);
        }

        TaskCompletionSource? completion = null;
        lock (_gate)
        {
            if (_pendingCompletion is not null && data.Span.SequenceEqual(_pendingBarrier.Span))
            {
                completion = _pendingCompletion;
                _pendingBarrier = default;
                _pendingCompletion = null;
            }
        }

        completion?.TrySetResult();
    }

    private void TryClearPhysicalScreen()
    {
        try
        {
            _clearPhysicalScreen?.Invoke();
        }
        catch (IOException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static ReadOnlyMemory<byte> CreateCleanScreenOverwrite(int width, int height)
    {
        var safeWidth = Math.Max(1, width);
        var safeHeight = Math.Max(1, height);
        var rowFill = new string(' ', Math.Max(0, safeWidth - 1));
        var builder = new System.Text.StringBuilder(
            ((safeWidth + 32) * safeHeight) + 8);
        for (var row = 1; row <= safeHeight; row++)
        {
            builder.Append("\x1b[");
            builder.Append(row);
            builder.Append(";1H");
            builder.Append("\x1b[2K");
            builder.Append(rowFill);
            builder.Append("\x1b[");
            builder.Append(row);
            builder.Append(';');
            builder.Append(safeWidth);
            builder.Append("H \x1b[1X");
        }

        builder.Append("\x1b[H");
        return System.Text.Encoding.UTF8.GetBytes(builder.ToString());
    }

    ValueTask<ReadOnlyMemory<byte>> IHex1bTerminalPresentationAdapter.ReadInputAsync(
        CancellationToken cancellationToken)
        => _inner.ReadInputAsync(cancellationToken);

    ValueTask IHex1bTerminalPresentationAdapter.FlushAsync(CancellationToken cancellationToken)
        => _inner.FlushAsync(cancellationToken);

    ValueTask IHex1bTerminalPresentationAdapter.EnterRawModeAsync(CancellationToken cancellationToken)
        => _inner.EnterRawModeAsync(cancellationToken);

    ValueTask IHex1bTerminalPresentationAdapter.ExitRawModeAsync(CancellationToken cancellationToken)
        => _inner.ExitRawModeAsync(cancellationToken);

    (int Row, int Column) IHex1bTerminalPresentationAdapter.GetCursorPosition()
        => _inner.GetCursorPosition();

    ReflowResult ITerminalReflowProvider.Reflow(ReflowContext context)
        => _inner is ITerminalReflowProvider reflowProvider
            ? reflowProvider.Reflow(context)
            : throw new InvalidOperationException("The wrapped presentation does not support reflow.");

    ValueTask IAsyncDisposable.DisposeAsync()
        => _inner.DisposeAsync();
}
