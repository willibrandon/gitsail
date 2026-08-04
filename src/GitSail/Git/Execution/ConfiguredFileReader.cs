using GitSail.Domain;
using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GitSail.Git.Execution;

/// <summary>
/// Reads bounded regular files selected by trusted Git configuration using exact native paths.
/// </summary>
internal static class ConfiguredFileReader
{
    private const int UnixErrorNotFound = 2;
    private const int UnixOpenReadOnly = 0;
    private const uint UnixFileTypeMask = 0x0000f000;
    private const uint UnixRegularFileType = 0x00008000;
    private const int UnixStatusBufferBytes = 256;

    /// <summary>
    /// Reads one configured regular file while following links according to ordinary file semantics.
    /// </summary>
    /// <param name="path">The exact absolute native path selected through Git configuration.</param>
    /// <param name="maximumBytes">The positive maximum accepted content length.</param>
    /// <param name="cancellationToken">Signals read cancellation.</param>
    /// <returns>The exact bytes, or <see langword="null"/> when the configured path does not exist.</returns>
    internal static Task<byte[]?> ReadIfExistsAsync(
        GitPath path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        return path.Kind switch
        {
            NativePathKind.UnixBytes when !OperatingSystem.IsWindows() =>
                ReadUnixIfExistsAsync(path, maximumBytes, cancellationToken),
            NativePathKind.WindowsUtf16 when OperatingSystem.IsWindows() =>
                ReadWindowsIfExistsAsync(path, maximumBytes, cancellationToken),
            _ => throw new PlatformNotSupportedException(
                "The configured file path kind does not match this operating system."),
        };
    }

    private static async Task<byte[]?> ReadUnixIfExistsAsync(
        GitPath path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var nativePath = path.GetUnixBytes();
        if (nativePath.IsEmpty || nativePath[0] != (byte)'/')
        {
            throw new InvalidDataException("A configured Unix file path must be absolute.");
        }

        var terminatedPath = new byte[nativePath.Length + 1];
        nativePath.CopyTo(terminatedPath);
        using var file = OpenUnixFile(terminatedPath);
        if (file is null)
        {
            return null;
        }

        EnsureUnixRegularFile(file);
        return await ReadBoundedHandleAsync(file, maximumBytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]?> ReadWindowsIfExistsAsync(
        GitPath path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var nativePath = path.GetWindowsPath();
        if (!Path.IsPathFullyQualified(nativePath))
        {
            throw new InvalidDataException("A configured Windows file path must be absolute.");
        }

        FileStream stream;
        try
        {
            stream = new FileStream(
                nativePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        await using (stream.ConfigureAwait(false))
        {
            if (!stream.CanSeek)
            {
                throw new IOException("The configured path is not a regular file.");
            }

            return await ReadBoundedStreamAsync(stream, maximumBytes, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<byte[]> ReadBoundedHandleAsync(
        SafeFileHandle file,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var length = RandomAccess.GetLength(file);
        ValidateLength(length, maximumBytes);
        var bytes = new byte[(int)length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await RandomAccess.ReadAsync(
                file,
                bytes.AsMemory(offset),
                offset,
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("The configured file changed while it was being read.");
            }

            offset += read;
        }

        var probe = new byte[1];
        if (await RandomAccess.ReadAsync(
                file,
                probe,
                offset,
                cancellationToken).ConfigureAwait(false) != 0)
        {
            throw new IOException("The configured file changed while it was being read.");
        }

        return bytes;
    }

    private static async Task<byte[]> ReadBoundedStreamAsync(
        FileStream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var length = stream.Length;
        ValidateLength(length, maximumBytes);
        var bytes = new byte[(int)length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("The configured file changed while it was being read.");
            }

            offset += read;
        }

        var probe = new byte[1];
        if (await stream.ReadAsync(probe, cancellationToken).ConfigureAwait(false) != 0)
        {
            throw new IOException("The configured file changed while it was being read.");
        }

        return bytes;
    }

    private static void ValidateLength(long length, int maximumBytes)
    {
        if (length < 0 || length > maximumBytes)
        {
            throw new InvalidDataException($"The configured file exceeds {maximumBytes} bytes.");
        }
    }

    private static unsafe SafeFileHandle? OpenUnixFile(byte[] path)
    {
        var flags = UnixOpenReadOnly | GetUnixCloseOnExecFlag() | GetUnixNonBlockingFlag();
        fixed (byte* pathPointer = path)
        {
            var fileDescriptor = UnixNative.Open(pathPointer, flags);
            if (fileDescriptor >= 0)
            {
                return new SafeFileHandle((nint)fileDescriptor, ownsHandle: true);
            }

            var error = Marshal.GetLastPInvokeError();
            if (error == UnixErrorNotFound)
            {
                return null;
            }

            throw new IOException(
                $"The configured file could not be opened ({error}).",
                new Win32Exception(error));
        }
    }

    private static unsafe void EnsureUnixRegularFile(SafeFileHandle file)
    {
        Span<byte> status = stackalloc byte[UnixStatusBufferBytes];
        fixed (byte* statusPointer = status)
        {
            if (UnixNative.FileStatus(
                    checked((int)file.DangerousGetHandle()),
                    statusPointer) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                throw new IOException(
                    $"The configured file type could not be read ({error}).",
                    new Win32Exception(error));
            }
        }

        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("GitSail supports only little-endian Unix RIDs.");
        }

        var mode = OperatingSystem.IsMacOS()
            ? BinaryPrimitives.ReadUInt16LittleEndian(status[4..])
            : BinaryPrimitives.ReadUInt32LittleEndian(status[24..]);
        if ((mode & UnixFileTypeMask) != UnixRegularFileType)
        {
            throw new IOException("The configured path is not a regular file.");
        }
    }

    private static int GetUnixCloseOnExecFlag()
        => OperatingSystem.IsMacOS() ? 0x01000000 : 0x00080000;

    private static int GetUnixNonBlockingFlag()
        => OperatingSystem.IsMacOS() ? 0x0004 : 0x00000800;
}
