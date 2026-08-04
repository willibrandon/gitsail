using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Implements the bounded nonce-authenticated helper pipe protocol shared by parent and child.
/// </summary>
internal static class CredentialPromptProtocol
{
    /// <summary>
    /// Identifies the current private helper protocol version.
    /// </summary>
    internal const string ProtocolVersion = "1";

    /// <summary>
    /// Identifies the only private helper role currently accepted by the application.
    /// </summary>
    internal const string HelperKind = "askpass";

    /// <summary>
    /// Names the private environment variable selecting the helper protocol version.
    /// </summary>
    internal const string ProtocolVariable = "GITSAIL_HELPER_PROTOCOL";

    /// <summary>
    /// Names the private environment variable selecting the helper role.
    /// </summary>
    internal const string KindVariable = "GITSAIL_HELPER_KIND";

    /// <summary>
    /// Names the private environment variable carrying the operation pipe name.
    /// </summary>
    internal const string EndpointVariable = "GITSAIL_HELPER_ENDPOINT";

    /// <summary>
    /// Names the private environment variable carrying the operation session identity.
    /// </summary>
    internal const string SessionVariable = "GITSAIL_HELPER_SESSION";

    /// <summary>
    /// Names the private environment variable carrying the one-operation nonce.
    /// </summary>
    internal const string NonceVariable = "GITSAIL_HELPER_NONCE";

    /// <summary>
    /// Names the private environment variable carrying the expected parent process identity.
    /// </summary>
    internal const string ParentProcessVariable = "GITSAIL_HELPER_PARENT_PID";

    /// <summary>
    /// Defines the inclusive prompt and response frame byte limit.
    /// </summary>
    internal const int MaximumTextBytes = 64 * 1024;
    private const int ChallengeLength = 32;
    private static readonly byte[] s_magic = "GITSAIL-PROMPT-V1"u8.ToArray();
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// Authenticates one connected helper from the operation-bound server side.
    /// </summary>
    /// <param name="stream">The connected user-only pipe.</param>
    /// <param name="sessionId">The exact operation session identity.</param>
    /// <param name="nonce">The operation-owned authentication nonce.</param>
    /// <param name="parentProcessId">The expected parent process identity.</param>
    /// <param name="cancellationToken">Signals handshake cancellation.</param>
    internal static async Task AuthenticateServerAsync(
        Stream stream,
        string sessionId,
        ReadOnlyMemory<byte> nonce,
        int parentProcessId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var magic = new byte[s_magic.Length];
        await stream.ReadExactlyAsync(magic, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(magic, s_magic))
        {
            throw new InvalidDataException("The credential helper protocol marker is invalid.");
        }

        var receivedParentProcessId = await ReadInt32Async(stream, cancellationToken).ConfigureAwait(false);
        var helperProcessId = await ReadInt32Async(stream, cancellationToken).ConfigureAwait(false);
        var receivedSession = await ReadTextAsync(stream, 256, cancellationToken).ConfigureAwait(false);
        if (receivedParentProcessId != parentProcessId || helperProcessId <= 0 ||
            !string.Equals(receivedSession, sessionId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The credential helper parent or session identity is invalid.");
        }

        var challenge = RandomNumberGenerator.GetBytes(ChallengeLength);
        try
        {
            await stream.WriteAsync(challenge, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            var suppliedMac = new byte[ChallengeLength];
            await stream.ReadExactlyAsync(suppliedMac, cancellationToken).ConfigureAwait(false);
            var expectedMac = ComputeAuthenticationCode(
                nonce.Span,
                challenge,
                sessionId,
                parentProcessId,
                helperProcessId);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(suppliedMac, expectedMac))
                {
                    throw new InvalidDataException("The credential helper authentication response is invalid.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(suppliedMac);
                CryptographicOperations.ZeroMemory(expectedMac);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(challenge);
        }
    }

    /// <summary>
    /// Authenticates one helper to the operation-bound parent pipe.
    /// </summary>
    /// <param name="stream">The connected user-only pipe.</param>
    /// <param name="sessionId">The exact operation session identity.</param>
    /// <param name="nonce">The operation authentication nonce.</param>
    /// <param name="parentProcessId">The declared parent process identity.</param>
    /// <param name="cancellationToken">Signals handshake cancellation.</param>
    internal static async Task AuthenticateClientAsync(
        Stream stream,
        string sessionId,
        ReadOnlyMemory<byte> nonce,
        int parentProcessId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        await stream.WriteAsync(s_magic, cancellationToken).ConfigureAwait(false);
        await WriteInt32Async(stream, parentProcessId, cancellationToken).ConfigureAwait(false);
        await WriteInt32Async(stream, Environment.ProcessId, cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(stream, sessionId, 256, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        var challenge = new byte[ChallengeLength];
        await stream.ReadExactlyAsync(challenge, cancellationToken).ConfigureAwait(false);
        try
        {
            var mac = ComputeAuthenticationCode(
                nonce.Span,
                challenge,
                sessionId,
                parentProcessId,
                Environment.ProcessId);
            try
            {
                await stream.WriteAsync(mac, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(mac);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(challenge);
        }
    }

    /// <summary>
    /// Writes one strict UTF-8 text frame under an explicit byte limit.
    /// </summary>
    /// <param name="stream">The authenticated pipe stream.</param>
    /// <param name="value">The text value.</param>
    /// <param name="maximumBytes">The inclusive frame byte limit.</param>
    /// <param name="cancellationToken">Signals write cancellation.</param>
    internal static async Task WriteTextAsync(
        Stream stream,
        string value,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(value);
        var bytes = s_strictUtf8.GetBytes(value);
        try
        {
            if (bytes.Length > maximumBytes)
            {
                throw new InvalidDataException(
                    $"A credential helper text frame exceeds {maximumBytes} bytes.");
            }

            await WriteBytesAsync(stream, bytes, maximumBytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>
    /// Reads one strict UTF-8 text frame under an explicit byte limit.
    /// </summary>
    /// <param name="stream">The authenticated pipe stream.</param>
    /// <param name="maximumBytes">The inclusive frame byte limit.</param>
    /// <param name="cancellationToken">Signals read cancellation.</param>
    /// <returns>The decoded text frame.</returns>
    internal static async Task<string> ReadTextAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadBytesAsync(stream, maximumBytes, cancellationToken).ConfigureAwait(false);
        try
        {
            return s_strictUtf8.GetString(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>
    /// Writes one bounded binary frame with a fixed little-endian length prefix.
    /// </summary>
    /// <param name="stream">The authenticated pipe stream.</param>
    /// <param name="value">The frame bytes.</param>
    /// <param name="maximumBytes">The inclusive frame byte limit.</param>
    /// <param name="cancellationToken">Signals write cancellation.</param>
    internal static async Task WriteBytesAsync(
        Stream stream,
        ReadOnlyMemory<byte> value,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (value.Length > maximumBytes)
        {
            throw new InvalidDataException(
                $"A credential helper binary frame exceeds {maximumBytes} bytes.");
        }

        await WriteInt32Async(stream, value.Length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(value, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one bounded binary frame with a fixed little-endian length prefix.
    /// </summary>
    /// <param name="stream">The authenticated pipe stream.</param>
    /// <param name="maximumBytes">The inclusive frame byte limit.</param>
    /// <param name="cancellationToken">Signals read cancellation.</param>
    /// <returns>Owned frame bytes.</returns>
    internal static async Task<byte[]> ReadBytesAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var length = await ReadInt32Async(stream, cancellationToken).ConfigureAwait(false);
        if (length < 0 || length > maximumBytes)
        {
            throw new InvalidDataException(
                $"A credential helper frame length must be between 0 and {maximumBytes} bytes.");
        }

        var value = new byte[length];
        await stream.ReadExactlyAsync(value, cancellationToken).ConfigureAwait(false);
        return value;
    }

    private static byte[] ComputeAuthenticationCode(
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> challenge,
        string sessionId,
        int parentProcessId,
        int helperProcessId)
    {
        var sessionBytes = s_strictUtf8.GetBytes(sessionId);
        var message = new byte[checked(challenge.Length + 8 + sessionBytes.Length)];
        challenge.CopyTo(message);
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(challenge.Length), parentProcessId);
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(challenge.Length + 4), helperProcessId);
        sessionBytes.CopyTo(message, challenge.Length + 8);
        try
        {
            return HMACSHA256.HashData(nonce, message);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sessionBytes);
            CryptographicOperations.ZeroMemory(message);
        }
    }

    private static async Task WriteInt32Async(
        Stream stream,
        int value,
        CancellationToken cancellationToken)
    {
        var bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ReadInt32Async(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var bytes = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }
}
