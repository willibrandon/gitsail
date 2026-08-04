using System.IO.Pipes;
using System.Security.Cryptography;

namespace GitSail.Git.Execution;

/// <summary>
/// Runs the short-lived askpass role through an authenticated parent or controlling terminal.
/// </summary>
internal static class CredentialPromptHelperClient
{
    private static readonly TimeSpan s_connectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_responseTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Requests one response, writes only that response to standard output, and returns helper status.
    /// </summary>
    /// <param name="arguments">The single prompt argument supplied by Git or SSH.</param>
    /// <param name="environment">The explicit private helper environment source.</param>
    /// <param name="cancellationToken">Signals helper cancellation.</param>
    /// <returns>The documented success or failure process exit code.</returns>
    internal static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        IProcessEnvironment environment,
        CancellationToken cancellationToken)
    {
        using var invocation = CredentialPromptHelperInvocation.Create(arguments, environment);
        var response = await RequestAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return ExitCodes.Failure;
        }

        try
        {
            var output = Console.OpenStandardOutput();
            await output.WriteAsync(response, cancellationToken).ConfigureAwait(false);
            await output.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            return ExitCodes.Success;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(response);
        }
    }

    /// <summary>
    /// Requests one owned response from the authenticated parent or secure terminal fallback.
    /// </summary>
    /// <param name="invocation">The validated helper invocation.</param>
    /// <param name="cancellationToken">Signals helper cancellation.</param>
    /// <returns>Owned UTF-8 response bytes, or <see langword="null"/> on cancellation.</returns>
    internal static async Task<byte[]?> RequestAsync(
        CredentialPromptHelperInvocation invocation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (invocation.HasAuthenticatedParent)
        {
            try
            {
                return await RequestFromParentAsync(invocation, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or
                UnauthorizedAccessException or InvalidDataException or CryptographicException)
            {
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

        return CredentialPromptTerminal.Read(invocation.Prompt, invocation.Kind);
    }

    private static async Task<byte[]?> RequestFromParentAsync(
        CredentialPromptHelperInvocation invocation,
        CancellationToken cancellationToken)
    {
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(s_connectTimeout);
        await using var pipe = new NamedPipeClientStream(
            ".",
            invocation.Endpoint!,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
        await CredentialPromptProtocol.AuthenticateClientAsync(
            pipe,
            invocation.SessionId!,
            invocation.Nonce,
            invocation.ParentProcessId!.Value,
            connectTimeout.Token).ConfigureAwait(false);
        await CredentialPromptProtocol.WriteTextAsync(
            pipe,
            invocation.Prompt,
            CredentialPromptProtocol.MaximumTextBytes,
            connectTimeout.Token).ConfigureAwait(false);
        using var responseTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        responseTimeout.CancelAfter(s_responseTimeout);
        var status = new byte[1];
        await pipe.ReadExactlyAsync(status, responseTimeout.Token).ConfigureAwait(false);
        return status[0] switch
        {
            0 => null,
            1 => await CredentialPromptProtocol.ReadBytesAsync(
                pipe,
                CredentialPromptProtocol.MaximumTextBytes,
                responseTimeout.Token).ConfigureAwait(false),
            _ => throw new InvalidDataException("The credential parent returned an invalid response status."),
        };
    }
}
