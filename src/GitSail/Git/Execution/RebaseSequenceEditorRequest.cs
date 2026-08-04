using GitSail.Domain;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Creates and consumes protected single-use requests for Git's sequence-editor callback.
/// </summary>
internal static class RebaseSequenceEditorRequest
{
    private const int MaximumCreateAttempts = 16;
    private const int MaximumRequestBytes = 1024 * 1024;
    private const int SecretBytes = 32;
    private const int MacBytes = 32;
    private const int HeaderBytes = 21;
    private static readonly byte[] s_magic = "GSRBSE01"u8.ToArray();

    /// <summary>
    /// Gets the environment variable containing the protected request path.
    /// </summary>
    internal const string RequestPathVariable = "GITSAIL_HELPER_REQUEST";

    /// <summary>
    /// Gets the environment variable containing the request authentication secret.
    /// </summary>
    internal const string RequestSecretVariable = "GITSAIL_HELPER_SECRET";

    /// <summary>
    /// Creates a protected request bound to one exact Git-owned todo path.
    /// </summary>
    /// <param name="expectedTodoPath">The exact allowlisted todo path resolved through Git.</param>
    /// <param name="timeProvider">The trusted clock used for the short request lifetime.</param>
    /// <param name="cancellationToken">Signals cancellation before the request becomes available.</param>
    /// <returns>The request environment values and exact cleanup path.</returns>
    internal static async Task<RebaseSequenceEditorRequestHandle> CreateAsync(
        GitPath expectedTodoPath,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedTodoPath);
        ArgumentNullException.ThrowIfNull(timeProvider);
        var secret = RandomNumberGenerator.GetBytes(SecretBytes);
        var expiresAt = timeProvider.GetUtcNow().AddMinutes(5).ToUnixTimeSeconds();
        var contents = Serialize(expectedTodoPath, expiresAt, secret);
        for (var attempt = 0; attempt < MaximumCreateAttempts; attempt++)
        {
            var pathText = Path.Combine(
                Path.GetTempPath(),
                $".gitsail-rebase-{Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()}.request");
            var path = CreateNativePath(pathText);
            if (await RepositoryStateFileSystem.TryWriteNewAsync(
                    path,
                    contents,
                    cancellationToken).ConfigureAwait(false))
            {
                return new RebaseSequenceEditorRequestHandle(
                    path,
                    pathText,
                    Convert.ToHexString(secret));
            }
        }

        throw new IOException("A protected sequence-editor request file could not be created.");
    }

    /// <summary>
    /// Authenticates and consumes one request before the supplied todo path is opened.
    /// </summary>
    /// <param name="requestPathText">The protected request path from the process environment.</param>
    /// <param name="secretText">The hexadecimal authentication secret from the process environment.</param>
    /// <param name="suppliedTodoPath">The exact absolute todo path supplied by Git.</param>
    /// <param name="timeProvider">The trusted clock used to reject expired requests.</param>
    /// <param name="cancellationToken">Signals request-read cancellation.</param>
    /// <returns>The authenticated expected todo path.</returns>
    internal static async Task<GitPath> ConsumeAsync(
        string requestPathText,
        string secretText,
        GitPath suppliedTodoPath,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestPathText);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretText);
        ArgumentNullException.ThrowIfNull(suppliedTodoPath);
        ArgumentNullException.ThrowIfNull(timeProvider);
        byte[] secret;
        try
        {
            secret = Convert.FromHexString(secretText);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The sequence-editor request secret is invalid.", exception);
        }

        if (secret.Length != SecretBytes)
        {
            throw new InvalidDataException("The sequence-editor request secret has an invalid length.");
        }

        var requestPath = CreateNativePath(Path.GetFullPath(requestPathText));
        var contents = await RepositoryStateFileSystem.ReadIfExistsAsync(
            requestPath,
            MaximumRequestBytes,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The sequence-editor request is missing or was already used.");
        var expectedPath = Authenticate(contents, secret, timeProvider);
        if (!await RepositoryStateFileSystem.DeleteIfExistsAsync(
                requestPath,
                cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The sequence-editor request could not be consumed exactly once.");
        }

        if (!expectedPath.Equals(suppliedTodoPath))
        {
            throw new InvalidDataException("Git supplied a todo path that does not match the authenticated request.");
        }

        return expectedPath;
    }

    /// <summary>
    /// Removes an unused request after Git exits without invoking the helper.
    /// </summary>
    /// <param name="handle">The request to remove.</param>
    /// <param name="cancellationToken">Signals cleanup cancellation.</param>
    /// <returns>A task that completes after best-effort exact-path cleanup.</returns>
    internal static async Task DeleteIfExistsAsync(
        RebaseSequenceEditorRequestHandle handle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        _ = await RepositoryStateFileSystem.DeleteIfExistsAsync(
            handle.FilePath,
            cancellationToken).ConfigureAwait(false);
    }

    private static byte[] Serialize(GitPath expectedPath, long expiresAt, ReadOnlySpan<byte> secret)
    {
        var pathBytes = expectedPath.Kind == NativePathKind.UnixBytes
            ? expectedPath.GetUnixBytes().ToArray()
            : Encoding.Unicode.GetBytes(expectedPath.GetWindowsPath());
        var unsigned = new byte[checked(HeaderBytes + pathBytes.Length)];
        s_magic.CopyTo(unsigned, 0);
        BinaryPrimitives.WriteInt64LittleEndian(unsigned.AsSpan(8, 8), expiresAt);
        unsigned[16] = (byte)expectedPath.Kind;
        BinaryPrimitives.WriteInt32LittleEndian(unsigned.AsSpan(17, 4), pathBytes.Length);
        pathBytes.CopyTo(unsigned, HeaderBytes);
        var result = new byte[checked(unsigned.Length + MacBytes)];
        unsigned.CopyTo(result, 0);
        HMACSHA256.HashData(secret, unsigned, result.AsSpan(unsigned.Length));
        return result;
    }

    private static GitPath Authenticate(
        ReadOnlySpan<byte> contents,
        ReadOnlySpan<byte> secret,
        TimeProvider timeProvider)
    {
        if (contents.Length < HeaderBytes + MacBytes ||
            !contents[..8].SequenceEqual(s_magic))
        {
            throw new InvalidDataException("The sequence-editor request format is invalid.");
        }

        var pathLength = BinaryPrimitives.ReadInt32LittleEndian(contents.Slice(17, 4));
        if (pathLength <= 0 || pathLength > MaximumRequestBytes - HeaderBytes - MacBytes ||
            contents.Length != HeaderBytes + pathLength + MacBytes)
        {
            throw new InvalidDataException("The sequence-editor request path length is invalid.");
        }

        Span<byte> actualMac = stackalloc byte[MacBytes];
        HMACSHA256.HashData(secret, contents[..^MacBytes], actualMac);
        if (!CryptographicOperations.FixedTimeEquals(actualMac, contents[^MacBytes..]))
        {
            throw new InvalidDataException("The sequence-editor request authentication failed.");
        }

        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var expiresAt = BinaryPrimitives.ReadInt64LittleEndian(contents.Slice(8, 8));
        if (expiresAt < now || expiresAt > now + 10 * 60)
        {
            throw new InvalidDataException("The sequence-editor request has expired.");
        }

        var kind = (NativePathKind)contents[16];
        var path = contents.Slice(HeaderBytes, pathLength);
        return kind switch
        {
            NativePathKind.UnixBytes when !OperatingSystem.IsWindows() => GitPath.FromUnixBytes(path),
            NativePathKind.WindowsUtf16 when OperatingSystem.IsWindows() && path.Length % 2 == 0 =>
                GitPath.FromWindowsPath(Encoding.Unicode.GetString(path)),
            _ => throw new InvalidDataException(
                "The sequence-editor request path kind does not match this operating system."),
        };
    }

    private static GitPath CreateNativePath(string path)
        => OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath(path)
            : GitPath.FromUnixBytes(Encoding.UTF8.GetBytes(path));
}
