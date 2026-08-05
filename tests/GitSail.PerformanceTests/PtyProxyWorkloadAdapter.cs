using System.Diagnostics;
using Hex1b;

namespace GitSail.PerformanceTests;

/// <summary>
/// Streams an application running beneath the test-owned musl PTY proxy into a headless terminal.
/// </summary>
internal sealed class PtyProxyWorkloadAdapter : IHex1bTerminalWorkloadAdapter
{
    private readonly Process _process;
    private readonly byte[] _readBuffer = new byte[8192];
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task<string>? _standardError;
    private bool _processStarted;
    private bool _disposed;

    /// <summary>
    /// Initializes a fixed-size real-PTY workload for one executable invocation.
    /// </summary>
    /// <param name="proxyPath">The absolute path to the compiled test PTY proxy.</param>
    /// <param name="executable">The absolute Native AOT executable path.</param>
    /// <param name="arguments">The exact application argument sequence.</param>
    /// <param name="workingDirectory">The exact child working directory.</param>
    /// <param name="width">The terminal width in cells.</param>
    /// <param name="height">The terminal height in cells.</param>
    internal PtyProxyWorkloadAdapter(
        string proxyPath,
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        int width,
        int height)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proxyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var startInfo = new ProcessStartInfo
        {
            FileName = proxyPath,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(width.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(height.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(executable);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["TERM"] = "xterm-256color";
        startInfo.Environment["COLORTERM"] = "truecolor";
        startInfo.Environment["COLUMNS"] = width.ToString(System.Globalization.CultureInfo.InvariantCulture);
        startInfo.Environment["LINES"] = height.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _process = new Process { StartInfo = startInfo };
    }

    /// <summary>
    /// Gets diagnostics emitted by the PTY proxy itself rather than by the child terminal stream.
    /// </summary>
    internal async Task<string> GetDiagnosticsAsync()
        => _standardError is null
            ? string.Empty
            : await _standardError.ConfigureAwait(false);

    /// <summary>
    /// Starts the PTY proxy and its Native AOT child.
    /// </summary>
    /// <param name="cancellationToken">Cancels startup before the process has been created.</param>
    internal Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_processStarted)
        {
            throw new InvalidOperationException("The PTY proxy has already been started.");
        }

        try
        {
            if (!_process.Start())
            {
                throw new InvalidOperationException($"Could not start PTY proxy '{_process.StartInfo.FileName}'.");
            }

            _processStarted = true;
            _standardError = _process.StandardError.ReadToEndAsync(CancellationToken.None);
            _started.TrySetResult();
            return Task.CompletedTask;
        }
        catch (Exception exception)
        {
            _started.TrySetException(exception);
            throw;
        }
    }

    /// <summary>
    /// Waits for the PTY proxy and its Native AOT child to exit.
    /// </summary>
    /// <param name="cancellationToken">Cancels only the wait operation.</param>
    /// <returns>The child-compatible proxy exit code.</returns>
    internal async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        await _started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        Disconnected?.Invoke();
        return _process.ExitCode;
    }

    /// <summary>
    /// Terminates the PTY proxy and its complete child process tree.
    /// </summary>
    internal void Kill()
    {
        if (_processStarted && !_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }
    }

    async ValueTask<ReadOnlyMemory<byte>> IHex1bTerminalWorkloadAdapter.ReadOutputAsync(
        CancellationToken ct)
    {
        await _started.Task.WaitAsync(ct).ConfigureAwait(false);
        var count = await _process.StandardOutput.BaseStream.ReadAsync(_readBuffer, ct).ConfigureAwait(false);
        return count == 0 ? ReadOnlyMemory<byte>.Empty : _readBuffer.AsMemory(0, count).ToArray();
    }

    async ValueTask IHex1bTerminalWorkloadAdapter.WriteInputAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken ct)
    {
        await _started.Task.WaitAsync(ct).ConfigureAwait(false);
        await _process.StandardInput.BaseStream.WriteAsync(data, ct).ConfigureAwait(false);
        await _process.StandardInput.BaseStream.FlushAsync(ct).ConfigureAwait(false);
    }

    ValueTask IHex1bTerminalWorkloadAdapter.ResizeAsync(
        int width,
        int height,
        CancellationToken ct)
        => ValueTask.CompletedTask;

    event Action? IHex1bTerminalWorkloadAdapter.Disconnected
    {
        add => Disconnected += value;
        remove => Disconnected -= value;
    }

    private event Action? Disconnected;

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_processStarted)
        {
            _started.TrySetCanceled();
        }

        if (_processStarted && !_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }

        if (_processStarted)
        {
            await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        _process.Dispose();
    }
}
