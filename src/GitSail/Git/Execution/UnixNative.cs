using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace GitSail.Git.Execution;

/// <summary>
/// Defines the fixed libc entry points required for exact Unix repository files.
/// </summary>
internal static unsafe partial class UnixNative
{
    /// <summary>
    /// Resolves a raw-byte absolute Unix path through libc canonicalization.
    /// </summary>
    /// <param name="path">The NUL-terminated native path bytes.</param>
    /// <param name="resolvedPath">An optional caller buffer or <see langword="null"/> for libc allocation.</param>
    /// <returns>The canonical NUL-terminated path or <see langword="null"/> on failure.</returns>
    [LibraryImport("libc", EntryPoint = "realpath", SetLastError = true)]
    internal static partial byte* RealPath(byte* path, byte* resolvedPath);

    /// <summary>
    /// Starts one Unix child through the Native AOT-linked runtime process PAL.
    /// </summary>
    /// <param name="filename">The NUL-terminated executable path.</param>
    /// <param name="arguments">The NUL-terminated native argument vector.</param>
    /// <param name="environment">The NUL-terminated native environment vector.</param>
    /// <param name="workingDirectory">The NUL-terminated native working directory.</param>
    /// <param name="redirectStandardInput">Whether to create a redirected standard-input pipe.</param>
    /// <param name="redirectStandardOutput">Whether to create a redirected standard-output pipe.</param>
    /// <param name="redirectStandardError">Whether to create a redirected standard-error pipe.</param>
    /// <param name="setCredentials">Whether the supplied user credentials must be applied.</param>
    /// <param name="userId">The child user identifier.</param>
    /// <param name="groupId">The child group identifier.</param>
    /// <param name="groups">The child supplementary group identifiers.</param>
    /// <param name="groupsLength">The supplementary group count.</param>
    /// <param name="childProcessId">Receives the started child process identifier.</param>
    /// <param name="standardInputFileDescriptor">Receives the parent standard-input descriptor.</param>
    /// <param name="standardOutputFileDescriptor">Receives the parent standard-output descriptor.</param>
    /// <param name="standardErrorFileDescriptor">Receives the parent standard-error descriptor.</param>
    /// <returns>Zero on success or -1 with errno captured on failure.</returns>
    [LibraryImport(
        "System.Native",
        EntryPoint = "SystemNative_ForkAndExecProcess",
        SetLastError = true)]
    internal static partial int ForkAndExecProcess(
        byte* filename,
        byte** arguments,
        byte** environment,
        byte* workingDirectory,
        int redirectStandardInput,
        int redirectStandardOutput,
        int redirectStandardError,
        int setCredentials,
        uint userId,
        uint groupId,
        uint* groups,
        int groupsLength,
        int* childProcessId,
        int* standardInputFileDescriptor,
        int* standardOutputFileDescriptor,
        int* standardErrorFileDescriptor);

    /// <summary>
    /// Waits for and reaps one exact Unix child process.
    /// </summary>
    /// <param name="processId">The child process identifier.</param>
    /// <param name="status">Receives the native wait status.</param>
    /// <param name="options">The native wait options.</param>
    /// <returns>The reaped process identifier or -1 on failure.</returns>
    [LibraryImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    internal static partial int WaitProcess(int processId, int* status, int options);

    /// <summary>
    /// Sends one native signal to a Unix process or process group.
    /// </summary>
    /// <param name="processId">A process identifier or negative process-group identifier.</param>
    /// <param name="signal">The native signal number.</param>
    /// <returns>Zero on success or -1 on failure.</returns>
    [LibraryImport("System.Native", EntryPoint = "SystemNative_Kill", SetLastError = true)]
    internal static partial int Kill(int processId, int signal);

    /// <summary>
    /// Opens a raw-byte Unix path through the .NET runtime portability layer.
    /// </summary>
    /// <param name="path">The NUL-terminated native path bytes.</param>
    /// <param name="flags">The stable portability-layer open flags.</param>
    /// <param name="mode">The creation mode, ignored unless the creation flag is present.</param>
    /// <returns>The nonnegative native handle or -1 on failure.</returns>
    [LibraryImport("System.Native", EntryPoint = "SystemNative_Open", SetLastError = true)]
    internal static partial nint OpenPortable(byte* path, int flags, int mode);

    /// <summary>
    /// Reads stable file status from an opened Unix handle through the .NET runtime portability layer.
    /// </summary>
    /// <param name="file">The opened Unix handle.</param>
    /// <param name="status">Receives portable file identity, type, and mode information.</param>
    /// <returns>Zero on success or -1 with errno captured on failure.</returns>
    [LibraryImport("System.Native", EntryPoint = "SystemNative_FStat", SetLastError = true)]
    internal static partial int FileStatusPortable(
        SafeFileHandle file,
        out UnixFileStatus status);

    /// <summary>
    /// Opens a raw-byte absolute path and captures errno on failure.
    /// </summary>
    /// <param name="path">The NUL-terminated native path bytes.</param>
    /// <param name="flags">The platform open flags.</param>
    /// <param name="mode">The creation mode, ignored unless the creation flag is present.</param>
    /// <returns>A nonnegative file descriptor or -1 on failure.</returns>
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true)]
    internal static partial int Open(byte* path, int flags, uint mode);

    /// <summary>
    /// Opens a raw-byte name relative to an already opened directory.
    /// </summary>
    /// <param name="directoryFileDescriptor">The opened parent directory descriptor.</param>
    /// <param name="path">The NUL-terminated relative path bytes.</param>
    /// <param name="flags">The platform open flags.</param>
    /// <param name="mode">The creation mode, ignored unless the creation flag is present.</param>
    /// <returns>A nonnegative file descriptor or -1 on failure.</returns>
    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true)]
    internal static partial int OpenAt(
        int directoryFileDescriptor,
        byte* path,
        int flags,
        uint mode);

    /// <summary>
    /// Atomically creates one absolute raw-byte Unix directory only when it is absent.
    /// </summary>
    /// <param name="path">The NUL-terminated absolute directory path.</param>
    /// <param name="mode">The initial directory permission bits.</param>
    /// <returns>Zero on success or -1 with errno captured on failure.</returns>
    [LibraryImport("libc", EntryPoint = "mkdir", SetLastError = true)]
    internal static partial int MakeDirectory(byte* path, uint mode);

    /// <summary>
    /// Reads the platform terminal attributes into a caller-owned ABI-sized buffer.
    /// </summary>
    /// <param name="fileDescriptor">The opened controlling-terminal descriptor.</param>
    /// <param name="attributes">The caller-owned terminal-attribute buffer.</param>
    /// <returns>Zero on success or -1 with errno captured on failure.</returns>
    [LibraryImport("libc", EntryPoint = "tcgetattr", SetLastError = true)]
    internal static partial int GetTerminalAttributes(int fileDescriptor, void* attributes);

    /// <summary>
    /// Applies caller-owned platform terminal attributes to an opened descriptor.
    /// </summary>
    /// <param name="fileDescriptor">The opened controlling-terminal descriptor.</param>
    /// <param name="actions">When the attribute change takes effect.</param>
    /// <param name="attributes">The caller-owned terminal-attribute buffer.</param>
    /// <returns>Zero on success or -1 with errno captured on failure.</returns>
    [LibraryImport("libc", EntryPoint = "tcsetattr", SetLastError = true)]
    internal static partial int SetTerminalAttributes(
        int fileDescriptor,
        int actions,
        void* attributes);

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
