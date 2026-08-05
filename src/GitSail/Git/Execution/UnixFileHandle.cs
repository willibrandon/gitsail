using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GitSail.Git.Execution;

/// <summary>
/// Opens and validates Unix directory handles through the .NET runtime portability layer.
/// </summary>
internal static class UnixFileHandle
{
    private const int ErrorNotFound = 2;
    private const int FileTypeMask = 0xf000;
    private const int DirectoryFileType = 0x4000;
    private const int OpenReadOnly = 0x0000;
    private const int OpenCloseOnExec = 0x0010;
    private const int OpenNoFollow = 0x0200;

    /// <summary>
    /// Opens one NUL-terminated raw-byte path as a no-follow directory handle.
    /// </summary>
    /// <param name="path">The NUL-terminated native path bytes.</param>
    /// <param name="failureMessage">The operation context used if the path cannot be opened or inspected.</param>
    /// <returns>The owned directory handle.</returns>
    internal static SafeFileHandle OpenDirectory(
        ReadOnlySpan<byte> path,
        string failureMessage)
        => OpenDirectoryCore(path, failureMessage, returnNullWhenNotFound: false)!;

    /// <summary>
    /// Opens one NUL-terminated raw-byte path as a no-follow directory handle when it exists.
    /// </summary>
    /// <param name="path">The NUL-terminated native path bytes.</param>
    /// <param name="failureMessage">The operation context used if the path cannot be opened or inspected.</param>
    /// <returns>The owned directory handle, or <see langword="null"/> when the path does not exist.</returns>
    internal static SafeFileHandle? OpenDirectoryIfExists(
        ReadOnlySpan<byte> path,
        string failureMessage)
        => OpenDirectoryCore(path, failureMessage, returnNullWhenNotFound: true);

    /// <summary>
    /// Reads portable identity and mode information from one opened Unix handle.
    /// </summary>
    /// <param name="file">The opened Unix file handle.</param>
    /// <param name="failureMessage">The operation context used if the handle cannot be inspected.</param>
    /// <returns>The portable file status.</returns>
    internal static UnixFileStatus GetStatus(
        SafeFileHandle file,
        string failureMessage)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureMessage);
        if (UnixNative.FileStatusPortable(file, out var status) == 0)
        {
            return status;
        }

        var error = Marshal.GetLastPInvokeError();
        throw new IOException(
            $"{failureMessage.TrimEnd('.')} ({error}).",
            new Win32Exception(error));
    }

    private static unsafe SafeFileHandle? OpenDirectoryCore(
        ReadOnlySpan<byte> path,
        string failureMessage,
        bool returnNullWhenNotFound)
    {
        if (path.IsEmpty || path[^1] != 0)
        {
            throw new ArgumentException("A native Unix path must be NUL-terminated.", nameof(path));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(failureMessage);
        nint descriptor;
        fixed (byte* pathPointer = path)
        {
            descriptor = UnixNative.OpenPortable(
                pathPointer,
                OpenReadOnly | OpenCloseOnExec | OpenNoFollow,
                mode: 0);
        }

        if (descriptor == -1)
        {
            var error = Marshal.GetLastPInvokeError();
            if (returnNullWhenNotFound && error == ErrorNotFound)
            {
                return null;
            }

            throw new IOException(
                $"{failureMessage.TrimEnd('.')} ({error}).",
                new Win32Exception(error));
        }

        var file = new SafeFileHandle(descriptor, ownsHandle: true);
        try
        {
            var status = GetStatus(file, $"{failureMessage} Its file type could not be read");
            if ((status.Mode & FileTypeMask) != DirectoryFileType)
            {
                throw new IOException($"{failureMessage} The path is not a no-follow directory.");
            }

            return file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }
}
