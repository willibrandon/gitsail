using System.Buffers;

namespace GitSail.Git.Execution;

/// <summary>
/// Retains exact sequential bytes in memory before spilling to a permission-restricted temporary file.
/// </summary>
internal sealed class RawByteSpool : IDisposable
{
    private readonly int _memoryThresholdBytes;
    private ArrayBufferWriter<byte>? _memory;
    private FileStream? _file;
    private string? _filePath;
    private bool _disposed;

    private RawByteSpool(int memoryThresholdBytes)
    {
        _memoryThresholdBytes = memoryThresholdBytes;
        _memory = new ArrayBufferWriter<byte>(Math.Min(memoryThresholdBytes, 16 * 1024));
    }

    /// <summary>
    /// Gets the exact byte count currently retained by the spool.
    /// </summary>
    internal long Length { get; private set; }

    /// <summary>
    /// Gets whether retained bytes have crossed the memory threshold and moved to a file.
    /// </summary>
    internal bool IsFileBacked => _file is not null;

    /// <summary>
    /// Creates an empty spool with a positive in-memory retention threshold.
    /// </summary>
    /// <param name="memoryThresholdBytes">The byte count after which storage spills to disk.</param>
    /// <returns>The empty owned spool.</returns>
    internal static RawByteSpool Create(int memoryThresholdBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(memoryThresholdBytes);
        return new RawByteSpool(memoryThresholdBytes);
    }

    /// <summary>
    /// Appends one exact byte segment without decoding or normalizing it.
    /// </summary>
    /// <param name="bytes">The byte segment to append.</param>
    /// <param name="cancellationToken">Signals asynchronous file-write cancellation.</param>
    /// <returns>A task that completes after the segment is retained.</returns>
    internal async ValueTask AppendAsync(
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (bytes.IsEmpty)
        {
            return;
        }

        if (_file is null && checked(Length + bytes.Length) > _memoryThresholdBytes)
        {
            await SpillToFileAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_file is null)
        {
            _memory!.Write(bytes.Span);
        }
        else
        {
            await _file.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }

        Length = checked(Length + bytes.Length);
    }

    /// <summary>
    /// Opens an independent read-only stream over the complete retained byte sequence.
    /// </summary>
    /// <returns>A seekable stream positioned at byte zero.</returns>
    internal Stream OpenRead()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_file is null)
        {
            return new MemoryStream(_memory!.WrittenSpan.ToArray(), writable: false);
        }

        _file.Flush();
        return new FileStream(
            _filePath!,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
    }

    /// <summary>
    /// Reads one bounded exact slice from the retained sequence.
    /// </summary>
    /// <param name="offset">The nonnegative byte offset.</param>
    /// <param name="length">The nonnegative requested byte count.</param>
    /// <param name="cancellationToken">Signals read cancellation.</param>
    /// <returns>The exact requested slice.</returns>
    internal async Task<byte[]> ReadSliceAsync(
        long offset,
        int length,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var result = new byte[length];
        await ReadSliceAsync(offset, result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Reads one exact bounded slice directly into caller-owned destination memory.
    /// </summary>
    /// <param name="offset">The nonnegative byte offset.</param>
    /// <param name="destination">The exact destination whose length defines the requested slice.</param>
    /// <param name="cancellationToken">Signals read cancellation.</param>
    /// <returns>A task that completes after the destination is filled.</returns>
    internal async Task ReadSliceAsync(
        long offset,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset > Length || destination.Length > Length - offset)
        {
            throw new ArgumentOutOfRangeException(nameof(destination));
        }

        await using var stream = OpenRead();
        stream.Position = offset;
        await stream.ReadExactlyAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes retained storage and removes any temporary file created by this spool.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _file?.Dispose();
        _file = null;
        _memory = null;
        if (_filePath is not null)
        {
            try
            {
                File.Delete(_filePath);
            }
            catch (FileNotFoundException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }

            _filePath = null;
        }
    }

    private async Task SpillToFileAsync(CancellationToken cancellationToken)
    {
        (_file, _filePath) = CreateTemporaryFile();
        if (_memory!.WrittenCount > 0)
        {
            await _file.WriteAsync(_memory.WrittenMemory, cancellationToken).ConfigureAwait(false);
        }

        _memory = null;
    }

    private static (FileStream Stream, string Path) CreateTemporaryFile()
    {
        var directory = Path.GetTempPath();
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var path = Path.Combine(directory, $"gitsail-spool-{Guid.NewGuid():N}.tmp");
            try
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.Read | FileShare.Delete,
                    BufferSize = 64 * 1024,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                };
                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }

                return (new FileStream(path, options), path);
            }
            catch (IOException) when (attempt < 15)
            {
            }
        }

        throw new IOException("A unique raw-byte spool file could not be created.");
    }
}
