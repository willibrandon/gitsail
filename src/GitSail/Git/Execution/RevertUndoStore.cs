using GitSail.Domain;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Persists one bounded checksummed revert recovery in the private platform user cache.
/// </summary>
internal sealed class RevertUndoStore
{
    private const int MaximumPatchBytes = 1024 * 1024 * 1024;
    private const int RepositoryIdBytes = 32;
    private const int IndexFingerprintBytes = 32;
    private const int ChecksumBytes = 32;
    private const int MaximumHeadTextBytes = 64;
    private const int MaximumHeadNameBytes = ushort.MaxValue;
    private const int FixedRecordBytes = 8 + 8 + RepositoryIdBytes + 1 + sizeof(ushort) + IndexFingerprintBytes + 4 + ChecksumBytes;
    private const int MaximumRecordBytes = FixedRecordBytes + MaximumHeadTextBytes + MaximumHeadNameBytes + MaximumPatchBytes;
    private static readonly byte[] s_magic = "GSRUNDO2"u8.ToArray();
    private static readonly TimeSpan s_retention = TimeSpan.FromHours(24);
    private static readonly TimeSpan s_futureClockTolerance = TimeSpan.FromMinutes(5);
    private readonly GitPath _recoveryPath;
    private readonly string _undoDirectory;
    private readonly byte[] _repositoryId;
    private readonly TimeProvider _timeProvider;

    private RevertUndoStore(
        GitPath recoveryPath,
        string undoDirectory,
        byte[] repositoryId,
        TimeProvider timeProvider)
    {
        _recoveryPath = recoveryPath;
        _undoDirectory = undoDirectory;
        _repositoryId = repositoryId;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Gets a control-safe warning produced while cleaning invalid or expired recovery state.
    /// </summary>
    internal string? Warning { get; private set; }

    /// <summary>
    /// Gets the opaque current-repository recovery path for verification and diagnostics.
    /// </summary>
    internal GitPath RecoveryPath => _recoveryPath;

    /// <summary>
    /// Creates a repository-scoped store and performs bounded cleanup of every cached undo record.
    /// </summary>
    /// <param name="repository">The canonical repository locations used only for opaque keyed identity.</param>
    /// <param name="environment">The classified process environment used for user directories.</param>
    /// <param name="timeProvider">The UTC clock used for creation and retention decisions.</param>
    /// <param name="cancellationToken">Signals identity creation and cleanup cancellation.</param>
    /// <returns>The initialized current-repository recovery store.</returns>
    internal static async Task<RevertUndoStore> CreateAsync(
        RepositoryLocation repository,
        IProcessEnvironment environment,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(timeProvider);
        var directoryPathService = new UserDirectoryPathService(environment);
        var cacheDirectory = directoryPathService.GetCacheDirectory();
        var undoDirectory = Path.Combine(cacheDirectory, "undo");
        UserDirectoryFileSystem.EnsurePrivateDirectory(cacheDirectory);
        UserDirectoryFileSystem.EnsurePrivateDirectory(undoDirectory);
        var repositoryIdText = await new RepositoryIdentityService(directoryPathService)
            .GetIdAsync(repository, cancellationToken)
            .ConfigureAwait(false);
        var repositoryId = Convert.FromHexString(repositoryIdText);
        var recoveryPath = CreatePath(Path.Combine(
            undoDirectory,
            $"revert-{repositoryIdText}.bin"));
        var store = new RevertUndoStore(
            recoveryPath,
            undoDirectory,
            repositoryId,
            timeProvider);
        await store.CleanupAsync(cancellationToken).ConfigureAwait(false);
        return store;
    }

    /// <summary>
    /// Creates an immutable in-memory recovery state with the store's current UTC time.
    /// </summary>
    /// <param name="patch">The nonempty exact forward patch that restores reverted worktree bytes.</param>
    /// <param name="precondition">The live HEAD object, attachment, and staged-index identity captured before revert.</param>
    /// <returns>The new one-level revert recovery state.</returns>
    internal RevertUndoState CreateState(
        ReadOnlySpan<byte> patch,
        RepositoryPrecondition precondition)
        => new(patch, precondition, _timeProvider.GetUtcNow());

    /// <summary>
    /// Atomically persists one current-repository recovery record with user-only file permissions.
    /// </summary>
    /// <param name="state">The exact in-memory recovery state to persist.</param>
    /// <param name="cancellationToken">Signals cancellation before atomic replacement.</param>
    /// <returns>A task that completes after the recovery record is durable.</returns>
    internal Task SaveAsync(
        RevertUndoState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        var record = Serialize(state, _repositoryId);
        return RepositoryStateFileSystem.WriteAtomicallyAsync(
            _recoveryPath,
            record,
            cancellationToken);
    }

    /// <summary>
    /// Loads the current unexpired checksummed recovery or safely discards invalid state.
    /// </summary>
    /// <param name="cancellationToken">Signals bounded no-follow read or deletion cancellation.</param>
    /// <returns>The recovered state, or <see langword="null"/> when no eligible record exists.</returns>
    internal async Task<RevertUndoState?> LoadAsync(CancellationToken cancellationToken)
    {
        var record = await RepositoryStateFileSystem.ReadIfExistsAsync(
            _recoveryPath,
            MaximumRecordBytes,
            cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }

        RevertUndoState state;
        try
        {
            state = Deserialize(record, _repositoryId);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            Warning = "Discarded an invalid cached revert recovery record.";
            await TryDeleteAsync(_recoveryPath, cancellationToken).ConfigureAwait(false);
            return null;
        }

        var now = _timeProvider.GetUtcNow();
        if (IsExpired(state.CreatedAtUtc, now) || state.CreatedAtUtc - now > s_futureClockTolerance)
        {
            if (state.CreatedAtUtc - now > s_futureClockTolerance)
            {
                Warning = "Discarded a cached revert recovery record with an invalid future timestamp.";
            }

            await TryDeleteAsync(_recoveryPath, cancellationToken).ConfigureAwait(false);
            return null;
        }

        return state;
    }

    /// <summary>
    /// Removes the current repository's exact no-follow recovery record when present.
    /// </summary>
    /// <param name="cancellationToken">Signals cancellation before identity-checked deletion.</param>
    /// <returns>A task that completes after the recovery is absent.</returns>
    internal async Task DiscardAsync(CancellationToken cancellationToken)
        => _ = await RepositoryStateFileSystem.DeleteIfExistsAsync(
            _recoveryPath,
            cancellationToken).ConfigureAwait(false);

    private async Task CleanupAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var filePath in Directory.EnumerateFiles(
                     _undoDirectory,
                     "revert-*.bin",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = CreatePath(filePath);
            try
            {
                var record = await RepositoryStateFileSystem.ReadIfExistsAsync(
                    path,
                    MaximumRecordBytes,
                    cancellationToken).ConfigureAwait(false);
                if (record is null)
                {
                    continue;
                }

                var state = Deserialize(record, expectedRepositoryId: null);
                if (IsExpired(state.CreatedAtUtc, now) || state.CreatedAtUtc - now > s_futureClockTolerance)
                {
                    await TryDeleteAsync(path, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
            {
                Warning ??= "Discarded one invalid cached revert recovery record.";
                await TryDeleteAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Warning ??= "One invalid cached revert recovery record could not be cleaned automatically.";
            }
        }
    }

    private async Task TryDeleteAsync(GitPath path, CancellationToken cancellationToken)
    {
        try
        {
            _ = await RepositoryStateFileSystem.DeleteIfExistsAsync(
                path,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Warning ??= "A cached revert recovery record could not be removed automatically.";
        }
    }

    private static byte[] Serialize(RevertUndoState state, ReadOnlySpan<byte> repositoryId)
    {
        if (state.Patch.Length is <= 0 or > MaximumPatchBytes)
        {
            throw new InvalidDataException($"A revert recovery patch cannot exceed {MaximumPatchBytes} bytes.");
        }

        var headText = state.Precondition.HeadObjectId?.ToString() ?? string.Empty;
        var headBytes = Encoding.ASCII.GetBytes(headText);
        var headNameBytes = state.Precondition.HeadName?.GetBytes().ToArray() ?? [];
        if (headNameBytes.Length > MaximumHeadNameBytes)
        {
            throw new InvalidDataException(
                $"A revert recovery HEAD name cannot exceed {MaximumHeadNameBytes} bytes.");
        }

        var length = checked(
            FixedRecordBytes + headBytes.Length + headNameBytes.Length + state.Patch.Length);
        var record = new byte[length];
        var offset = 0;
        s_magic.CopyTo(record, offset);
        offset += s_magic.Length;
        BinaryPrimitives.WriteInt64LittleEndian(
            record.AsSpan(offset, sizeof(long)),
            state.CreatedAtUtc.ToUnixTimeSeconds());
        offset += sizeof(long);
        repositoryId.CopyTo(record.AsSpan(offset, RepositoryIdBytes));
        offset += RepositoryIdBytes;
        record[offset++] = checked((byte)headBytes.Length);
        headBytes.CopyTo(record, offset);
        offset += headBytes.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(
            record.AsSpan(offset, sizeof(ushort)),
            checked((ushort)headNameBytes.Length));
        offset += sizeof(ushort);
        headNameBytes.CopyTo(record, offset);
        offset += headNameBytes.Length;
        state.Precondition.IndexFingerprint.Span.CopyTo(
            record.AsSpan(offset, IndexFingerprintBytes));
        offset += IndexFingerprintBytes;
        BinaryPrimitives.WriteInt32LittleEndian(
            record.AsSpan(offset, sizeof(int)),
            state.Patch.Length);
        offset += sizeof(int);
        state.Patch.Span.CopyTo(record.AsSpan(offset, state.Patch.Length));
        offset += state.Patch.Length;
        SHA256.HashData(record.AsSpan(0, offset), record.AsSpan(offset, ChecksumBytes));
        return record;
    }

    private static RevertUndoState Deserialize(
        ReadOnlySpan<byte> record,
        byte[]? expectedRepositoryId)
    {
        if (record.Length < FixedRecordBytes || !record[..s_magic.Length].SequenceEqual(s_magic))
        {
            throw new InvalidDataException("The revert recovery header is invalid.");
        }

        var contentLength = record.Length - ChecksumBytes;
        Span<byte> actualChecksum = stackalloc byte[ChecksumBytes];
        SHA256.HashData(record[..contentLength], actualChecksum);
        if (!CryptographicOperations.FixedTimeEquals(actualChecksum, record[contentLength..]))
        {
            throw new InvalidDataException("The revert recovery checksum is invalid.");
        }

        var offset = s_magic.Length;
        DateTimeOffset createdAtUtc;
        try
        {
            createdAtUtc = DateTimeOffset.FromUnixTimeSeconds(
                BinaryPrimitives.ReadInt64LittleEndian(record.Slice(offset, sizeof(long))));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException("The revert recovery timestamp is invalid.", exception);
        }

        offset += sizeof(long);
        var repositoryId = record.Slice(offset, RepositoryIdBytes);
        offset += RepositoryIdBytes;
        if (expectedRepositoryId is { } expected &&
            !CryptographicOperations.FixedTimeEquals(repositoryId, expected))
        {
            throw new InvalidDataException("The revert recovery repository identity does not match.");
        }

        var headLength = record[offset++];
        if (headLength is not (0 or 40 or 64) ||
            offset + headLength + sizeof(ushort) + IndexFingerprintBytes + sizeof(int) > contentLength)
        {
            throw new InvalidDataException("The revert recovery HEAD identity is invalid.");
        }

        ObjectId? headObjectId = null;
        if (headLength != 0 &&
            !ObjectId.TryParseHex(record.Slice(offset, headLength), out headObjectId))
        {
            throw new InvalidDataException("The revert recovery HEAD object identifier is invalid.");
        }

        offset += headLength;
        var headNameLength = BinaryPrimitives.ReadUInt16LittleEndian(
            record.Slice(offset, sizeof(ushort)));
        offset += sizeof(ushort);
        if (offset + headNameLength + IndexFingerprintBytes + sizeof(int) > contentLength)
        {
            throw new InvalidDataException("The revert recovery HEAD attachment is invalid.");
        }

        RefName? headName = null;
        if (headNameLength != 0)
        {
            headName = RefName.FromBytes(record.Slice(offset, headNameLength));
        }

        offset += headNameLength;
        var indexFingerprint = record.Slice(offset, IndexFingerprintBytes);
        offset += IndexFingerprintBytes;
        var patchLength = BinaryPrimitives.ReadInt32LittleEndian(record.Slice(offset, sizeof(int)));
        offset += sizeof(int);
        if (patchLength is <= 0 or > MaximumPatchBytes || offset + patchLength != contentLength)
        {
            throw new InvalidDataException("The revert recovery patch length is invalid.");
        }

        var precondition = new RepositoryPrecondition(headObjectId, headName, indexFingerprint);
        return new RevertUndoState(record.Slice(offset, patchLength), precondition, createdAtUtc);
    }

    private static bool IsExpired(DateTimeOffset createdAtUtc, DateTimeOffset now)
        => now - createdAtUtc >= s_retention;

    private static GitPath CreatePath(string path)
        => OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath(path)
            : GitPath.FromUnixBytes(Encoding.UTF8.GetBytes(path));
}
