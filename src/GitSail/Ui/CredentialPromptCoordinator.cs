using GitSail.Git.Execution;
using System.Security.Cryptography;
using System.Text;

namespace GitSail.Ui;

/// <summary>
/// Serializes authenticated credential prompts into controlled nonpersistent UI state.
/// </summary>
internal sealed class CredentialPromptCoordinator : ICredentialPromptResponder, IDisposable
{
    private const int MaximumResponseBytes = 64 * 1024;
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _queue = new(1, 1);
    private TaskCompletionSource<byte[]?>? _response;
    private CredentialPromptRequest? _current;
    private long _nextRequestId;
    private volatile bool _disposed;

    /// <summary>
    /// Notifies the workspace that the current prompt changed.
    /// </summary>
    internal event Action? Changed;

    /// <summary>
    /// Gets the currently queued prompt, or no prompt while transport is not waiting.
    /// </summary>
    internal CredentialPromptRequest? Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    /// <summary>
    /// Queues one authenticated helper prompt and waits for the controlled user response.
    /// </summary>
    /// <param name="operation">The transport operation requesting the response.</param>
    /// <param name="prompt">The control-safe bounded prompt text.</param>
    /// <param name="kind">The required response treatment.</param>
    /// <param name="cancellationToken">Signals prompt cancellation.</param>
    /// <returns>Owned UTF-8 response bytes, or <see langword="null"/> when cancelled.</returns>
    public async Task<byte[]?> RequestAsync(
        string operation,
        string prompt,
        CredentialPromptKind kind,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _queue.WaitAsync(cancellationToken).ConfigureAwait(false);
        TaskCompletionSource<byte[]?> completion;
        try
        {
            completion = new TaskCompletionSource<byte[]?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _current = new CredentialPromptRequest(
                    Interlocked.Increment(ref _nextRequestId),
                    operation,
                    prompt,
                    kind);
                _response = completion;
            }

            Changed?.Invoke();
            using var cancellationRegistration = cancellationToken.Register(
                static state => ((TaskCompletionSource<byte[]?>)state!).TrySetCanceled(),
                completion);
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
            {
                _current = null;
                _response = null;
            }

            Changed?.Invoke();
            _queue.Release();
        }
    }

    /// <summary>
    /// Submits visible or secret text for the exact current prompt.
    /// </summary>
    /// <param name="requestId">The displayed prompt identity.</param>
    /// <param name="response">The entered response characters.</param>
    /// <returns><see langword="true"/> when the exact current prompt accepted the response.</returns>
    internal bool Submit(long requestId, ReadOnlySpan<char> response)
    {
        var byteCount = s_strictUtf8.GetByteCount(response);
        if (byteCount > MaximumResponseBytes)
        {
            throw new InvalidDataException(
                $"A credential response cannot exceed {MaximumResponseBytes} UTF-8 bytes.");
        }

        var bytes = new byte[byteCount];
        _ = s_strictUtf8.GetBytes(response, bytes);
        return SubmitOwned(requestId, bytes);
    }

    /// <summary>
    /// Transfers one owned UTF-8 response buffer to the exact current prompt.
    /// </summary>
    /// <param name="requestId">The displayed prompt identity.</param>
    /// <param name="response">The owned response buffer, which is cleared on rejection.</param>
    /// <returns><see langword="true"/> when ownership transferred to the exact current prompt.</returns>
    internal bool SubmitOwned(long requestId, byte[] response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Length > MaximumResponseBytes)
        {
            CryptographicOperations.ZeroMemory(response);
            throw new InvalidDataException(
                $"A credential response cannot exceed {MaximumResponseBytes} UTF-8 bytes.");
        }

        TaskCompletionSource<byte[]?>? completion;
        lock (_lock)
        {
            completion = _current?.Id == requestId ? _response : null;
        }

        if (completion is not null && completion.TrySetResult(response))
        {
            return true;
        }

        CryptographicOperations.ZeroMemory(response);
        return false;
    }

    /// <summary>
    /// Submits an explicit yes or no response for the exact current confirmation.
    /// </summary>
    /// <param name="requestId">The displayed prompt identity.</param>
    /// <param name="accepted">Whether the user accepted the transport question.</param>
    /// <returns><see langword="true"/> when the exact current prompt accepted the response.</returns>
    internal bool Confirm(long requestId, bool accepted)
        => Submit(requestId, accepted ? "yes" : "no");

    /// <summary>
    /// Cancels the exact current prompt without returning response bytes.
    /// </summary>
    /// <param name="requestId">The displayed prompt identity.</param>
    /// <returns><see langword="true"/> when the exact current prompt was cancelled.</returns>
    internal bool Cancel(long requestId)
    {
        TaskCompletionSource<byte[]?>? completion;
        lock (_lock)
        {
            completion = _current?.Id == requestId ? _response : null;
        }

        return completion?.TrySetResult(null) == true;
    }

    /// <summary>
    /// Cancels any pending prompt and releases synchronization resources.
    /// </summary>
    public void Dispose()
    {
        TaskCompletionSource<byte[]?>? completion;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            completion = _response;
        }

        _ = completion?.TrySetResult(null);
    }
}
