using GitSail.Domain;
using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Reads and atomically replaces bounded allowlisted repository files by exact native path.
/// </summary>
internal static class RepositoryStateFileSystem
{
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const int FileAttributeTagInfoClass = 9;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint CreateNew = 1;
    private const uint OpenExisting = 3;
    private const uint MoveFileReplaceExisting = 0x00000001;
    private const uint MoveFileWriteThrough = 0x00000008;
    private const int FileDispositionInfoClass = 4;
    private const int MaximumTemporaryCreateAttempts = 16;
    private const int UnixErrorNotFound = 2;
    private const int UnixOpenReadOnly = 0;
    private const int UnixOpenWriteOnly = 1;
    private const uint UnixOwnerReadWriteMode = 0x180;
    private const uint UnixFileTypeMask = 0x0000f000;
    private const uint UnixRegularFileType = 0x00008000;
    private const int UnixStatusBufferBytes = 256;

    /// <summary>
    /// Reads one regular no-follow file or reports that it does not exist.
    /// </summary>
    /// <param name="path">The exact native allowlisted path.</param>
    /// <param name="maximumBytes">The positive maximum accepted content length.</param>
    /// <param name="cancellationToken">Signals read cancellation.</param>
    /// <returns>The exact bytes, or <see langword="null"/> when the file does not exist.</returns>
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
            _ => throw new PlatformNotSupportedException("The native path kind does not match this operating system."),
        };
    }

    /// <summary>
    /// Durably replaces one allowlisted file through a same-directory temporary file.
    /// </summary>
    /// <param name="path">The exact native allowlisted destination path.</param>
    /// <param name="contents">The exact bytes to persist.</param>
    /// <param name="cancellationToken">Signals cancellation before atomic replacement.</param>
    /// <returns>A task that completes after the replacement is flushed.</returns>
    internal static Task WriteAtomicallyAsync(
        GitPath path,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Kind switch
        {
            NativePathKind.UnixBytes when !OperatingSystem.IsWindows() =>
                WriteUnixAtomicallyAsync(path, contents, unixMode: null, cancellationToken),
            NativePathKind.WindowsUtf16 when OperatingSystem.IsWindows() =>
                WriteWindowsAtomicallyAsync(path, contents, cancellationToken),
            _ => throw new PlatformNotSupportedException("The native path kind does not match this operating system."),
        };
    }

    /// <summary>
    /// Durably replaces one regular worktree file with exact bytes and the selected canonical mode.
    /// </summary>
    /// <param name="path">The exact absolute worktree destination path.</param>
    /// <param name="contents">The exact filtered worktree bytes to persist.</param>
    /// <param name="mode">The regular or executable Git file mode.</param>
    /// <param name="cancellationToken">Signals cancellation before atomic replacement.</param>
    /// <returns>A task that completes after the replacement is flushed.</returns>
    internal static Task WriteWorkTreeFileAtomicallyAsync(
        GitPath path,
        ReadOnlyMemory<byte> contents,
        GitFileMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        var unixMode = mode switch
        {
            GitFileMode.RegularFile => 0x1a4u,
            GitFileMode.ExecutableFile => 0x1edu,
            _ => throw new ArgumentOutOfRangeException(
                nameof(mode),
                "Atomic worktree replacement supports only regular file modes."),
        };
        return path.Kind switch
        {
            NativePathKind.UnixBytes when !OperatingSystem.IsWindows() =>
                WriteUnixAtomicallyAsync(path, contents, unixMode, cancellationToken),
            NativePathKind.WindowsUtf16 when OperatingSystem.IsWindows() =>
                WriteWindowsAtomicallyAsync(path, contents, cancellationToken),
            _ => throw new PlatformNotSupportedException("The native path kind does not match this operating system."),
        };
    }

    /// <summary>
    /// Durably creates one new regular no-follow file without replacing an existing entry.
    /// </summary>
    /// <param name="path">The exact native destination path.</param>
    /// <param name="contents">The exact bytes to persist in the new file.</param>
    /// <param name="cancellationToken">Signals cancellation before the new file becomes durable.</param>
    /// <returns><see langword="true"/> when created; <see langword="false"/> when the path already exists.</returns>
    internal static Task<bool> TryWriteNewAsync(
        GitPath path,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Kind switch
        {
            NativePathKind.UnixBytes when !OperatingSystem.IsWindows() =>
                TryWriteNewUnixAsync(path, contents, cancellationToken),
            NativePathKind.WindowsUtf16 when OperatingSystem.IsWindows() =>
                TryWriteNewWindowsAsync(path, contents, cancellationToken),
            _ => throw new PlatformNotSupportedException("The native path kind does not match this operating system."),
        };
    }

    /// <summary>
    /// Deletes one exact regular no-follow file after identity validation.
    /// </summary>
    /// <param name="path">The exact native allowlisted path.</param>
    /// <param name="cancellationToken">Signals cancellation before deletion.</param>
    /// <returns><see langword="true"/> when a file was deleted; otherwise <see langword="false"/>.</returns>
    internal static Task<bool> DeleteIfExistsAsync(
        GitPath path,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        cancellationToken.ThrowIfCancellationRequested();
        return path.Kind switch
        {
            NativePathKind.UnixBytes when !OperatingSystem.IsWindows() =>
                Task.FromResult(DeleteUnixIfExists(path, cancellationToken)),
            NativePathKind.WindowsUtf16 when OperatingSystem.IsWindows() =>
                Task.FromResult(DeleteWindowsIfExists(path, cancellationToken)),
            _ => throw new PlatformNotSupportedException("The native path kind does not match this operating system."),
        };
    }

    private static async Task<byte[]?> ReadUnixIfExistsAsync(
        GitPath path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var (parentPath, fileName) = SplitUnixPath(path);
        using var parent = OpenUnixParentIfExists(parentPath);
        if (parent is null)
        {
            return null;
        }

        using var file = OpenUnixReadFile(parent, fileName);
        if (file is null)
        {
            return null;
        }

        return await ReadBoundedHandleAsync(file, maximumBytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteUnixAtomicallyAsync(
        GitPath path,
        ReadOnlyMemory<byte> contents,
        uint? unixMode,
        CancellationToken cancellationToken)
    {
        var (parentPath, fileName) = SplitUnixPath(path);
        using var parent = OpenUnixParent(parentPath);
        var parentFileDescriptor = GetFileDescriptor(parent);
        byte[]? temporaryName = null;
        try
        {
            using (var temporaryFile = CreateUnixTemporaryFile(parent, out temporaryName))
            {
                if (unixMode is { } requestedMode &&
                    UnixNative.ChangeMode(GetFileDescriptor(temporaryFile), requestedMode) != 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    throw CreateNativeIOException("The worktree temporary file mode could not be applied.", error);
                }

                await RandomAccess.WriteAsync(
                    temporaryFile,
                    contents,
                    fileOffset: 0,
                    cancellationToken).ConfigureAwait(false);
                RandomAccess.FlushToDisk(temporaryFile);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var preparedName = temporaryName
                ?? throw new InvalidOperationException("The repository state temporary name was not retained.");
            RenameUnix(parentFileDescriptor, preparedName, fileName);
            temporaryName = null;
            FlushUnixDirectory(parentFileDescriptor);
        }
        finally
        {
            if (temporaryName is not null)
            {
                UnlinkUnixTemporary(parentFileDescriptor, temporaryName);
            }
        }
    }

    private static async Task<bool> TryWriteNewUnixAsync(
        GitPath path,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken)
    {
        var (parentPath, fileName) = SplitUnixPath(path);
        using var parent = OpenUnixParent(parentPath);
        var parentFileDescriptor = GetFileDescriptor(parent);
        byte[]? temporaryName = null;
        try
        {
            using (var file = CreateUnixTemporaryFile(parent, out temporaryName))
            {
                await RandomAccess.WriteAsync(
                    file,
                    contents,
                    fileOffset: 0,
                    cancellationToken).ConfigureAwait(false);
                RandomAccess.FlushToDisk(file);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var preparedName = temporaryName
                ?? throw new InvalidOperationException("The protected state temporary name was not retained.");
            var created = LinkUnixNoReplace(parentFileDescriptor, preparedName, fileName);
            UnlinkUnixTemporary(parentFileDescriptor, preparedName);
            temporaryName = null;
            FlushUnixDirectory(parentFileDescriptor);
            return created;
        }
        finally
        {
            if (temporaryName is not null)
            {
                UnlinkUnixTemporary(parentFileDescriptor, temporaryName);
            }
        }
    }

    private static async Task<byte[]?> ReadWindowsIfExistsAsync(
        GitPath path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var filePath = path.GetWindowsPath();
        if (!Path.IsPathFullyQualified(filePath))
        {
            throw new InvalidDataException("A Windows repository state path must be absolute.");
        }

        using var file = WindowsNative.CreateFile(
            filePath,
            GenericRead,
            FileShareRead | FileShareWrite | FileShareDelete,
            0,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagSequentialScan,
            0);
        if (file.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is ErrorFileNotFound or ErrorPathNotFound)
            {
                return null;
            }

            throw CreateNativeIOException("The repository state file could not be opened.", error);
        }

        EnsureWindowsRegularFile(file);
        return await ReadBoundedHandleAsync(file, maximumBytes, cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureWindowsRegularFile(SafeFileHandle file)
    {
        var informationResult = WindowsNative.GetFileInformationByHandleEx(
            file,
            FileAttributeTagInfoClass,
            out var information,
            (uint)Marshal.SizeOf<FileAttributeTagInformation>());
        if (informationResult == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw CreateNativeIOException("The repository state file attributes could not be read.", error);
        }

        if ((information._fileAttributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0)
        {
            throw new IOException("The repository state path is not a regular no-follow file.");
        }
    }

    private static async Task WriteWindowsAtomicallyAsync(
        GitPath path,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken)
    {
        var destinationPath = path.GetWindowsPath();
        if (!Path.IsPathFullyQualified(destinationPath))
        {
            throw new InvalidDataException("A Windows repository state path must be absolute.");
        }

        var parentPath = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidDataException("The repository state path has no parent directory.");
        string? temporaryPath = null;
        try
        {
            SafeFileHandle? temporaryFile = null;
            for (var attempt = 0; attempt < MaximumTemporaryCreateAttempts; attempt++)
            {
                var candidatePath = Path.Combine(parentPath, CreateTemporaryName());
                temporaryFile = WindowsNative.CreateFile(
                    candidatePath,
                    GenericWrite,
                    shareMode: 0,
                    0,
                    CreateNew,
                    FileAttributeNormal | FileFlagWriteThrough,
                    0);
                if (!temporaryFile.IsInvalid)
                {
                    temporaryPath = candidatePath;
                    break;
                }

                var error = Marshal.GetLastPInvokeError();
                temporaryFile.Dispose();
                temporaryFile = null;
                if (error != 80 && error != 183)
                {
                    throw CreateNativeIOException("The repository state temporary file could not be created.", error);
                }
            }

            if (temporaryFile is null)
            {
                throw new IOException("A unique repository state temporary file could not be created.");
            }

            using (temporaryFile)
            {
                await RandomAccess.WriteAsync(
                    temporaryFile,
                    contents,
                    fileOffset: 0,
                    cancellationToken).ConfigureAwait(false);
                RandomAccess.FlushToDisk(temporaryFile);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (WindowsNative.MoveFileEx(
                    temporaryPath!,
                    destinationPath,
                    MoveFileReplaceExisting | MoveFileWriteThrough) == 0)
            {
                var error = Marshal.GetLastPInvokeError();
                throw CreateNativeIOException("The repository state file could not be atomically replaced.", error);
            }

            temporaryPath = null;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                _ = WindowsNative.DeleteFile(temporaryPath);
            }
        }
    }

    private static async Task<bool> TryWriteNewWindowsAsync(
        GitPath path,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken)
    {
        var destinationPath = path.GetWindowsPath();
        if (!Path.IsPathFullyQualified(destinationPath))
        {
            throw new InvalidDataException("A Windows protected state path must be absolute.");
        }

        var file = WindowsNative.CreateFile(
            destinationPath,
            GenericWrite,
            shareMode: 0,
            0,
            CreateNew,
            FileAttributeNormal | FileFlagOpenReparsePoint | FileFlagWriteThrough,
            0);
        if (file.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            file.Dispose();
            return error is 80 or 183
                ? false
                : throw CreateNativeIOException("The protected state file could not be created.", error);
        }

        var created = true;
        try
        {
            await RandomAccess.WriteAsync(
                file,
                contents,
                fileOffset: 0,
                cancellationToken).ConfigureAwait(false);
            RandomAccess.FlushToDisk(file);
            file.Dispose();
            created = false;
            return true;
        }
        finally
        {
            file.Dispose();
            if (created)
            {
                _ = WindowsNative.DeleteFile(destinationPath);
            }
        }
    }

    private static bool DeleteUnixIfExists(GitPath path, CancellationToken cancellationToken)
    {
        var (parentPath, fileName) = SplitUnixPath(path);
        using var parent = OpenUnixParentIfExists(parentPath);
        if (parent is null)
        {
            return false;
        }

        using var file = OpenUnixReadFile(parent, fileName);
        if (file is null)
        {
            return false;
        }

        var openedIdentity = GetUnixOpenedIdentity(file);
        var namedIdentity = GetUnixNamedIdentity(parent, fileName);
        if (openedIdentity != namedIdentity)
        {
            throw new IOException("The repository state file identity changed before deletion.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        UnlinkUnixFile(GetFileDescriptor(parent), fileName);
        FlushUnixDirectory(GetFileDescriptor(parent));
        return true;
    }

    private static bool DeleteWindowsIfExists(GitPath path, CancellationToken cancellationToken)
    {
        var filePath = path.GetWindowsPath();
        if (!Path.IsPathFullyQualified(filePath))
        {
            throw new InvalidDataException("A Windows repository state path must be absolute.");
        }

        using var file = WindowsNative.CreateFile(
            filePath,
            GenericRead | DeleteAccess,
            FileShareRead | FileShareWrite | FileShareDelete,
            0,
            OpenExisting,
            FileFlagOpenReparsePoint,
            0);
        if (file.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is ErrorFileNotFound or ErrorPathNotFound)
            {
                return false;
            }

            throw CreateNativeIOException("The repository state file could not be opened for deletion.", error);
        }

        EnsureWindowsRegularFile(file);
        cancellationToken.ThrowIfCancellationRequested();
        var information = new FileDispositionInformation
        {
            _deleteFile = 1,
        };
        if (WindowsNative.SetFileInformationByHandle(
                file,
                FileDispositionInfoClass,
                ref information,
                (uint)Marshal.SizeOf<FileDispositionInformation>()) == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw CreateNativeIOException("The repository state file could not be deleted.", error);
        }

        return true;
    }

    private static async Task<byte[]> ReadBoundedHandleAsync(
        SafeFileHandle file,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var length = RandomAccess.GetLength(file);
        if (length < 0 || length > maximumBytes)
        {
            throw new InvalidDataException($"The repository state file exceeds {maximumBytes} bytes.");
        }

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
                throw new IOException("The repository state file changed while it was being read.");
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
            throw new IOException("The repository state file changed while it was being read.");
        }

        return bytes;
    }

    private static SafeFileHandle OpenUnixParent(byte[] parentPath)
        => UnixFileHandle.OpenDirectory(
            parentPath,
            "The repository state parent directory could not be opened.");

    private static SafeFileHandle? OpenUnixParentIfExists(byte[] parentPath)
        => UnixFileHandle.OpenDirectoryIfExists(
            parentPath,
            "The repository state parent directory could not be opened.");

    private static unsafe SafeFileHandle? OpenUnixReadFile(SafeFileHandle parent, byte[] fileName)
    {
        var flags = UnixOpenReadOnly | GetUnixCloseOnExecFlag() | GetUnixNoFollowFlag() | GetUnixNonBlockingFlag();
        fixed (byte* namePointer = fileName)
        {
            var fileDescriptor = UnixNative.OpenAt(
                GetFileDescriptor(parent),
                namePointer,
                flags,
                mode: 0);
            if (fileDescriptor >= 0)
            {
                return new SafeFileHandle((nint)fileDescriptor, ownsHandle: true);
            }

            var error = Marshal.GetLastPInvokeError();
            if (error == UnixErrorNotFound)
            {
                return null;
            }

            throw CreateNativeIOException("The repository state file could not be opened.", error);
        }
    }

    private static unsafe SafeFileHandle CreateUnixTemporaryFile(
        SafeFileHandle parent,
        out byte[]? temporaryName)
    {
        temporaryName = null;
        if (OperatingSystem.IsMacOS())
        {
            var candidateName = Encoding.ASCII.GetBytes(
                ".gitsail-XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX.tmp\0");
            fixed (byte* namePointer = candidateName)
            {
                var fileDescriptor = UnixNative.CreateTemporaryAtMacOS(
                    GetFileDescriptor(parent),
                    namePointer,
                    suffixLength: 4);
                if (fileDescriptor < 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    throw CreateNativeIOException(
                        "The repository state temporary file could not be created.",
                        error);
                }

                temporaryName = candidateName;
                return new SafeFileHandle((nint)fileDescriptor, ownsHandle: true);
            }
        }

        var flags = UnixOpenWriteOnly |
            GetUnixCreateFlag() |
            GetUnixExclusiveFlag() |
            GetUnixCloseOnExecFlag() |
            GetUnixNoFollowFlag();
        for (var attempt = 0; attempt < MaximumTemporaryCreateAttempts; attempt++)
        {
            var candidateName = ToNullTerminated(Encoding.ASCII.GetBytes(CreateTemporaryName()));
            fixed (byte* namePointer = candidateName)
            {
                var fileDescriptor = UnixNative.CreateAtLinux(
                    GetFileDescriptor(parent),
                    namePointer,
                    flags,
                    UnixOwnerReadWriteMode);
                if (fileDescriptor >= 0)
                {
                    temporaryName = candidateName;
                    return new SafeFileHandle((nint)fileDescriptor, ownsHandle: true);
                }

                var error = Marshal.GetLastPInvokeError();
                if (error != 17)
                {
                    throw CreateNativeIOException("The repository state temporary file could not be created.", error);
                }
            }
        }

        throw new IOException("A unique repository state temporary file could not be created.");
    }

    private static unsafe bool LinkUnixNoReplace(
        int parentFileDescriptor,
        byte[] oldName,
        byte[] newName)
    {
        fixed (byte* oldNamePointer = oldName)
        fixed (byte* newNamePointer = newName)
        {
            if (UnixNative.LinkAt(
                    parentFileDescriptor,
                    oldNamePointer,
                    parentFileDescriptor,
                    newNamePointer,
                    flags: 0) == 0)
            {
                return true;
            }

            var error = Marshal.GetLastPInvokeError();
            if (error == 17)
            {
                return false;
            }

            throw CreateNativeIOException("The protected state file could not be published.", error);
        }
    }

    private static unsafe void RenameUnix(int parentFileDescriptor, byte[] oldName, byte[] newName)
    {
        fixed (byte* oldNamePointer = oldName)
        fixed (byte* newNamePointer = newName)
        {
            if (UnixNative.RenameAt(
                    parentFileDescriptor,
                    oldNamePointer,
                    parentFileDescriptor,
                    newNamePointer) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                throw CreateNativeIOException("The repository state file could not be atomically replaced.", error);
            }
        }
    }

    private static unsafe void UnlinkUnixTemporary(int parentFileDescriptor, byte[] temporaryName)
    {
        fixed (byte* namePointer = temporaryName)
        {
            _ = UnixNative.UnlinkAt(parentFileDescriptor, namePointer, flags: 0);
        }
    }

    private static unsafe void UnlinkUnixFile(int parentFileDescriptor, byte[] fileName)
    {
        fixed (byte* namePointer = fileName)
        {
            if (UnixNative.UnlinkAt(parentFileDescriptor, namePointer, flags: 0) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                throw CreateNativeIOException("The repository state file could not be deleted.", error);
            }
        }
    }

    private static (ulong Device, ulong Inode, uint Mode) GetUnixOpenedIdentity(SafeFileHandle file)
    {
        var status = UnixFileHandle.GetStatus(
            file,
            "The repository state file identity could not be read.");
        var mode = unchecked((uint)status.Mode);
        if ((mode & UnixFileTypeMask) != UnixRegularFileType)
        {
            throw new IOException("The repository state path is not a regular no-follow file.");
        }

        return (
            unchecked((ulong)status.Device),
            unchecked((ulong)status.Inode),
            mode);
    }

    private static unsafe (ulong Device, ulong Inode, uint Mode) GetUnixNamedIdentity(
        SafeFileHandle parent,
        byte[] fileName)
    {
        Span<byte> status = stackalloc byte[UnixStatusBufferBytes];
        fixed (byte* namePointer = fileName)
        fixed (byte* statusPointer = status)
        {
            if (UnixNative.FileStatusAt(
                    GetFileDescriptor(parent),
                    namePointer,
                    statusPointer,
                    GetUnixNoFollowStatusFlag()) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                throw CreateNativeIOException("The repository state path identity could not be read.", error);
            }
        }

        return ParseUnixIdentity(status);
    }

    private static (ulong Device, ulong Inode, uint Mode) ParseUnixIdentity(ReadOnlySpan<byte> status)
    {
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("GitSail supports only little-endian Unix RIDs.");
        }

        var device = OperatingSystem.IsMacOS()
            ? BinaryPrimitives.ReadUInt32LittleEndian(status)
            : BinaryPrimitives.ReadUInt64LittleEndian(status);
        var inode = BinaryPrimitives.ReadUInt64LittleEndian(status[8..]);
        var mode = OperatingSystem.IsMacOS()
            ? BinaryPrimitives.ReadUInt16LittleEndian(status[4..])
            : RuntimeInformation.OSArchitecture == Architecture.Arm64
                ? BinaryPrimitives.ReadUInt32LittleEndian(status[16..])
                : BinaryPrimitives.ReadUInt32LittleEndian(status[24..]);
        if ((mode & UnixFileTypeMask) != UnixRegularFileType)
        {
            throw new IOException("The repository state path is not a regular no-follow file.");
        }

        return (device, inode, mode);
    }

    private static void FlushUnixDirectory(int parentFileDescriptor)
    {
        if (UnixNative.FSync(parentFileDescriptor) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw CreateNativeIOException("The repository state parent directory could not be flushed.", error);
        }
    }

    private static (byte[] ParentPath, byte[] FileName) SplitUnixPath(GitPath path)
    {
        var bytes = path.GetUnixBytes();
        if (bytes.IsEmpty || bytes[0] != (byte)'/')
        {
            throw new InvalidDataException("A Unix repository state path must be absolute.");
        }

        var separator = bytes.LastIndexOf((byte)'/');
        if (separator < 0 || separator == bytes.Length - 1)
        {
            throw new InvalidDataException("The repository state path has no file name.");
        }

        var parent = separator == 0 ? bytes[..1] : bytes[..separator];
        return (ToNullTerminated(parent), ToNullTerminated(bytes[(separator + 1)..]));
    }

    private static byte[] ToNullTerminated(ReadOnlySpan<byte> bytes)
    {
        var result = new byte[bytes.Length + 1];
        bytes.CopyTo(result);
        return result;
    }

    private static string CreateTemporaryName()
        => $".gitsail-{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}.tmp";

    private static int GetFileDescriptor(SafeFileHandle file)
        => checked((int)file.DangerousGetHandle());

    private static int GetUnixCreateFlag()
        => OperatingSystem.IsMacOS() ? 0x0200 : 0x0040;

    private static int GetUnixExclusiveFlag()
        => OperatingSystem.IsMacOS() ? 0x0800 : 0x0080;

    private static int GetUnixCloseOnExecFlag()
        => OperatingSystem.IsMacOS() ? 0x01000000 : 0x00080000;

    private static int GetUnixNoFollowFlag()
        => OperatingSystem.IsMacOS()
            ? 0x0100
            : RuntimeInformation.RuntimeIdentifier.StartsWith("linux-musl-", StringComparison.Ordinal) &&
                RuntimeInformation.OSArchitecture == Architecture.Arm64
                ? 0x00008000
                : 0x00020000;

    private static int GetUnixNonBlockingFlag()
        => OperatingSystem.IsMacOS() ? 0x0004 : 0x00000800;

    private static int GetUnixNoFollowStatusFlag()
        => OperatingSystem.IsMacOS() ? 0x0020 : 0x0100;

    private static IOException CreateNativeIOException(string message, int error)
        => new($"{message} ({error}).", new Win32Exception(error));
}
