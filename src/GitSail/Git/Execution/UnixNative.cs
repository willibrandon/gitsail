using System.Runtime.InteropServices;

namespace GitSail.Git.Execution;

/// <summary>
/// Defines the fixed libc entry points required for exact Unix repository files.
/// </summary>
internal static unsafe partial class UnixNative
{
    /// <summary>
    /// Opens a raw-byte absolute path and captures errno on failure.
    /// </summary>
    /// <param name="path">The NUL-terminated native path bytes.</param>
    /// <param name="flags">The platform open flags.</param>
    /// <returns>A nonnegative file descriptor or -1 on failure.</returns>
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true)]
    internal static partial int Open(byte* path, int flags);

    /// <summary>
    /// Opens a raw-byte name relative to an already opened directory.
    /// </summary>
    /// <param name="directoryFileDescriptor">The opened parent directory descriptor.</param>
    /// <param name="path">The NUL-terminated relative path bytes.</param>
    /// <param name="flags">The platform open flags.</param>
    /// <returns>A nonnegative file descriptor or -1 on failure.</returns>
    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true)]
    internal static partial int OpenAt(
        int directoryFileDescriptor,
        byte* path,
        int flags);

    /// <summary>
    /// Creates a Linux raw-byte name relative to an already opened directory.
    /// </summary>
    /// <param name="directoryFileDescriptor">The opened parent directory descriptor.</param>
    /// <param name="path">The NUL-terminated relative path bytes.</param>
    /// <param name="flags">The platform creation flags.</param>
    /// <param name="mode">The creation permission bits.</param>
    /// <returns>A nonnegative file descriptor or -1 on failure.</returns>
    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true)]
    internal static partial int CreateAtLinux(
        int directoryFileDescriptor,
        byte* path,
        int flags,
        uint mode);

    /// <summary>
    /// Creates a mode-0600 macOS temporary file relative to an opened directory.
    /// </summary>
    /// <param name="directoryFileDescriptor">The opened parent directory descriptor.</param>
    /// <param name="pathTemplate">The mutable NUL-terminated XXXXXX template.</param>
    /// <param name="suffixLength">The suffix length following the template placeholders.</param>
    /// <returns>A nonnegative file descriptor or -1 on failure.</returns>
    [LibraryImport("libc", EntryPoint = "mkstempsat_np", SetLastError = true)]
    internal static partial int CreateTemporaryAtMacOS(
        int directoryFileDescriptor,
        byte* pathTemplate,
        int suffixLength);

    /// <summary>
    /// Atomically renames one raw-byte name between opened directories.
    /// </summary>
    /// <param name="oldDirectoryFileDescriptor">The opened source parent descriptor.</param>
    /// <param name="oldPath">The NUL-terminated source name.</param>
    /// <param name="newDirectoryFileDescriptor">The opened destination parent descriptor.</param>
    /// <param name="newPath">The NUL-terminated destination name.</param>
    /// <returns>Zero on success or -1 on failure.</returns>
    [LibraryImport("libc", EntryPoint = "renameat", SetLastError = true)]
    internal static partial int RenameAt(
        int oldDirectoryFileDescriptor,
        byte* oldPath,
        int newDirectoryFileDescriptor,
        byte* newPath);

    /// <summary>
    /// Atomically publishes one existing raw-byte file under a new directory-relative name.
    /// </summary>
    /// <param name="oldDirectoryFileDescriptor">The opened source parent descriptor.</param>
    /// <param name="oldPath">The NUL-terminated existing source name.</param>
    /// <param name="newDirectoryFileDescriptor">The opened destination parent descriptor.</param>
    /// <param name="newPath">The NUL-terminated new destination name.</param>
    /// <param name="flags">The fixed link behavior flags.</param>
    /// <returns>Zero on success or -1 on failure.</returns>
    [LibraryImport("libc", EntryPoint = "linkat", SetLastError = true)]
    internal static partial int LinkAt(
        int oldDirectoryFileDescriptor,
        byte* oldPath,
        int newDirectoryFileDescriptor,
        byte* newPath,
        int flags);

    /// <summary>
    /// Removes one raw-byte name relative to an already opened directory.
    /// </summary>
    /// <param name="directoryFileDescriptor">The opened parent descriptor.</param>
    /// <param name="path">The NUL-terminated relative path bytes.</param>
    /// <param name="flags">The unlink behavior flags.</param>
    /// <returns>Zero on success or -1 on failure.</returns>
    [LibraryImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    internal static partial int UnlinkAt(int directoryFileDescriptor, byte* path, int flags);

    /// <summary>
    /// Flushes file or directory metadata through one opened descriptor.
    /// </summary>
    /// <param name="fileDescriptor">The opened descriptor to flush.</param>
    /// <returns>Zero on success or -1 on failure.</returns>
    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    internal static partial int FSync(int fileDescriptor);

    /// <summary>
    /// Applies exact Unix permission bits to one already opened regular file.
    /// </summary>
    /// <param name="fileDescriptor">The opened file descriptor.</param>
    /// <param name="mode">The exact permission and executable bits.</param>
    /// <returns>Zero on success or -1 on failure.</returns>
    [LibraryImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    internal static partial int ChangeMode(int fileDescriptor, uint mode);

    /// <summary>
    /// Reads identity and mode metadata from one opened Unix descriptor.
    /// </summary>
    /// <param name="fileDescriptor">The opened descriptor.</param>
    /// <param name="status">Receives the platform stat structure bytes.</param>
    /// <returns>Zero on success or -1 on failure.</returns>
    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    internal static partial int FileStatus(int fileDescriptor, byte* status);

    /// <summary>
    /// Reads no-follow identity and mode metadata for a directory-relative name.
    /// </summary>
    /// <param name="directoryFileDescriptor">The opened parent directory descriptor.</param>
    /// <param name="path">The NUL-terminated relative path bytes.</param>
    /// <param name="status">Receives the platform stat structure bytes.</param>
    /// <param name="flags">The platform no-follow metadata flags.</param>
    /// <returns>Zero on success or -1 on failure.</returns>
    [LibraryImport("libc", EntryPoint = "fstatat", SetLastError = true)]
    internal static partial int FileStatusAt(
        int directoryFileDescriptor,
        byte* path,
        byte* status,
        int flags);
}
