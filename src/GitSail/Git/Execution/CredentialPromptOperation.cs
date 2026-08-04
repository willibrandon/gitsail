using System.Globalization;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Owns one fresh authenticated helper endpoint for one bounded transport operation.
/// </summary>
internal sealed class CredentialPromptOperation : IAsyncDisposable
{
    private static readonly TimeSpan s_clientTimeout = TimeSpan.FromMinutes(5);
    private readonly ICredentialPromptResponder _responder;
    private readonly string _operation;
    private readonly string _pipeName;
    private readonly string _sessionId;
    private readonly byte[] _nonce;
    private readonly int _parentProcessId;
    private readonly string? _helperExecutablePath;
    private readonly CancellationTokenSource _cancellationSource;
    private readonly Task _serverTask;
    private int _disposed;

    private CredentialPromptOperation(
        ICredentialPromptResponder responder,
        string operation,
        string? helperExecutablePath,
        CancellationToken cancellationToken)
    {
        _responder = responder;
        _operation = operation;
        _helperExecutablePath = helperExecutablePath;
        _parentProcessId = Environment.ProcessId;
        _sessionId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        _pipeName = $"gs-{_parentProcessId.ToString(CultureInfo.InvariantCulture)}-" +
            Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        _nonce = RandomNumberGenerator.GetBytes(32);
        _cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _serverTask = RunServerAsync(_cancellationSource.Token);
    }

    /// <summary>
    /// Starts one operation-scoped user-only pipe before the transport child can launch.
    /// </summary>
    /// <param name="responder">The controlled response source.</param>
    /// <param name="operation">The control-safe transport operation label.</param>
    /// <param name="helperExecutablePath">An explicit trusted current-executable test seam, or the runtime process path.</param>
    /// <param name="cancellationToken">Signals operation cancellation.</param>
    /// <returns>The active operation endpoint.</returns>
    internal static CredentialPromptOperation Start(
        ICredentialPromptResponder responder,
        string operation,
        string? helperExecutablePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(responder);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        return new CredentialPromptOperation(
            responder,
            operation,
            helperExecutablePath,
            cancellationToken);
    }

    /// <summary>
    /// Adds only the authenticated helper variables required by Git and OpenSSH.
    /// </summary>
    /// <param name="environment">The isolated base transport environment.</param>
    /// <returns>The complete operation-scoped transport environment.</returns>
    internal ChildEnvironment ConfigureEnvironment(ChildEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var processPath = _helperExecutablePath ?? Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            throw new InvalidOperationException(
                "The current executable path is unavailable for authenticated credential prompting.");
        }

        var helperPath = Path.GetFullPath(processPath);
        var nonce = Convert.ToBase64String(_nonce)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return environment
            .SetValue("GIT_ASKPASS", helperPath)
            .SetValue("SSH_ASKPASS", helperPath)
            .SetValue("SSH_ASKPASS_REQUIRE", "force")
            .SetValue("DISPLAY", "gitsail-authenticated-askpass")
            .SetValue(CredentialPromptProtocol.ProtocolVariable, CredentialPromptProtocol.ProtocolVersion)
            .SetValue(CredentialPromptProtocol.KindVariable, CredentialPromptProtocol.HelperKind)
            .SetValue(CredentialPromptProtocol.EndpointVariable, _pipeName)
            .SetValue(CredentialPromptProtocol.SessionVariable, _sessionId)
            .SetValue(CredentialPromptProtocol.NonceVariable, nonce)
            .SetValue(
                CredentialPromptProtocol.ParentProcessVariable,
                _parentProcessId.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Cancels the endpoint, clears the nonce, and waits for the accept loop to stop.
    /// </summary>
    /// <returns>A value task completing after endpoint shutdown.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _cancellationSource.CancelAsync().ConfigureAwait(false);
        try
        {
            await _serverTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cancellationSource.IsCancellationRequested)
        {
        }
        finally
        {
            CryptographicOperations.ZeroMemory(_nonce);
            _cancellationSource.Dispose();
        }
    }

    private async Task RunServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            using var clientTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            clientTimeout.CancelAfter(s_clientTimeout);
            try
            {
                await HandleClientAsync(pipe, clientTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidDataException or
                IOException or UnauthorizedAccessException or DecoderFallbackException or
                CryptographicException or OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
            }
        }
    }

    private async Task HandleClientAsync(Stream stream, CancellationToken cancellationToken)
    {
        await CredentialPromptProtocol.AuthenticateServerAsync(
            stream,
            _sessionId,
            _nonce,
            _parentProcessId,
            cancellationToken).ConfigureAwait(false);
        var prompt = await CredentialPromptProtocol.ReadTextAsync(
            stream,
            CredentialPromptProtocol.MaximumTextBytes,
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new InvalidDataException("The credential helper supplied an empty prompt.");
        }

        var safePrompt = CredentialPromptTextSanitizer.Sanitize(prompt);
        var response = await _responder.RequestAsync(
            _operation,
            safePrompt,
            CredentialPromptClassifier.Classify(safePrompt),
            cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(
                response is null ? new byte[] { 0 } : new byte[] { 1 },
                cancellationToken).ConfigureAwait(false);
            if (response is not null)
            {
                await CredentialPromptProtocol.WriteBytesAsync(
                    stream,
                    response,
                    CredentialPromptProtocol.MaximumTextBytes,
                    cancellationToken).ConfigureAwait(false);
            }

            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (response is not null)
            {
                CryptographicOperations.ZeroMemory(response);
            }
        }
    }
}
