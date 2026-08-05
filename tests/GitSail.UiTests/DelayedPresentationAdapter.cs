using Hex1b;

namespace GitSail.UiTests;

/// <summary>
/// Delays every presentation write so terminal-output ordering is deterministic in tests.
/// </summary>
internal sealed class DelayedPresentationAdapter : IHex1bTerminalPresentationAdapter
{
    private readonly HeadlessPresentationAdapter _inner;
    private readonly TimeSpan _writeDelay;
    private readonly Lock _writeGate = new();
    private readonly List<ReadOnlyMemory<byte>> _writes = [];

    /// <summary>
    /// Initializes a delayed headless terminal presentation with emulated screen restoration.
    /// </summary>
    /// <param name="width">The terminal width in columns.</param>
    /// <param name="height">The terminal height in rows.</param>
    /// <param name="writeDelay">The delay applied before each presentation write.</param>
    internal DelayedPresentationAdapter(int width, int height, TimeSpan writeDelay)
    {
        _writeDelay = writeDelay;
        _inner = new HeadlessPresentationAdapter(
            width,
            height,
            new TerminalCapabilities
            {
                SupportsMouse = true,
                SupportsTrueColor = true,
                Supports256Colors = true,
                SupportsAlternateScreen = true,
                HandlesAlternateScreenNatively = false,
                SupportsBracketedPaste = true,
                SupportsStyledUnderlines = true,
                SupportsUnderlineColor = true,
            });
    }

    /// <summary>
    /// Captures an immutable snapshot of the exact writes received by the presentation.
    /// </summary>
    /// <returns>The writes in physical terminal order.</returns>
    internal IReadOnlyList<ReadOnlyMemory<byte>> CaptureWrites()
    {
        lock (_writeGate)
        {
            return [.. _writes];
        }
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

    async ValueTask IHex1bTerminalPresentationAdapter.WriteOutputAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        await Task.Delay(_writeDelay, cancellationToken);
        await _inner.WriteOutputAsync(data, cancellationToken);
        lock (_writeGate)
        {
            _writes.Add(data.ToArray());
        }
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

    ValueTask IAsyncDisposable.DisposeAsync()
        => _inner.DisposeAsync();
}
