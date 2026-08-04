using GitSail.Domain;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Produces stable opaque repository identifiers with a private per-user HMAC key.
/// </summary>
internal sealed class RepositoryIdentityService
{
    private const int KeyBytes = 32;
    private const int MaximumKeyReadAttempts = 20;
    private static readonly byte[] s_domain = "GitSail repository identity v1\0"u8.ToArray();
    private readonly UserDirectoryPathService _directoryPathService;

    /// <summary>
    /// Initializes opaque repository identity generation over platform user directories.
    /// </summary>
    /// <param name="directoryPathService">The platform user-directory resolver.</param>
    internal RepositoryIdentityService(UserDirectoryPathService directoryPathService)
    {
        ArgumentNullException.ThrowIfNull(directoryPathService);
        _directoryPathService = directoryPathService;
    }

    /// <summary>
    /// Computes a stable opaque identifier without placing repository paths in a filename.
    /// </summary>
    /// <param name="repository">The canonical repository locations to identify.</param>
    /// <param name="cancellationToken">Signals private-key creation or read cancellation.</param>
    /// <returns>A lowercase 256-bit keyed repository identifier.</returns>
    internal async Task<string> GetIdAsync(
        RepositoryLocation repository,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var key = await LoadOrCreateKeyAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var hash = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
            hash.AppendData(s_domain);
            AppendPath(hash, repository.CommonDirectory);
            AppendPath(hash, repository.GitDirectory);
            AppendPath(hash, repository.WorkTree);
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private async Task<byte[]> LoadOrCreateKeyAsync(CancellationToken cancellationToken)
    {
        var configurationDirectory = _directoryPathService.GetConfigurationDirectory();
        UserDirectoryFileSystem.EnsurePrivateDirectory(configurationDirectory);
        var keyPath = CreatePath(Path.Combine(configurationDirectory, "repository-id.key"));
        for (var attempt = 0; attempt < MaximumKeyReadAttempts; attempt++)
        {
            var existing = await RepositoryStateFileSystem.ReadIfExistsAsync(
                keyPath,
                KeyBytes,
                cancellationToken).ConfigureAwait(false);
            if (existing is { Length: KeyBytes })
            {
                return existing;
            }

            if (existing is null)
            {
                var generated = RandomNumberGenerator.GetBytes(KeyBytes);
                if (await RepositoryStateFileSystem.TryWriteNewAsync(
                        keyPath,
                        generated,
                        cancellationToken).ConfigureAwait(false))
                {
                    return generated;
                }

                CryptographicOperations.ZeroMemory(generated);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidDataException(
            "The private repository identity key is incomplete or has an invalid length.");
    }

    private static void AppendPath(IncrementalHash hash, GitPath? path)
    {
        Span<byte> header = stackalloc byte[5];
        if (path is null)
        {
            hash.AppendData(header);
            return;
        }

        byte[] bytes;
        if (path.Kind == NativePathKind.UnixBytes)
        {
            bytes = path.GetUnixBytes().ToArray();
            header[0] = 1;
        }
        else
        {
            bytes = Encoding.Unicode.GetBytes(path.GetWindowsPath());
            header[0] = 2;
        }

        BinaryPrimitives.WriteInt32LittleEndian(header[1..], bytes.Length);
        hash.AppendData(header);
        hash.AppendData(bytes);
    }

    private static GitPath CreatePath(string path)
        => OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath(path)
            : GitPath.FromUnixBytes(Encoding.UTF8.GetBytes(path));
}
