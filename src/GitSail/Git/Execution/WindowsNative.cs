using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace GitSail.Git.Execution;

/// <summary>
/// Defines fixed Kernel32 entry points for handle-based UTF-16 repository files.
/// </summary>
internal static partial class WindowsNative
{
    /// <summary>
    /// Atomically creates one absolute UTF-16 directory only when it is absent.
    /// </summary>
    /// <param name="pathName">The absolute UTF-16 directory path.</param>
    /// <param name="securityAttributes">An unused security-attributes pointer.</param>
    /// <returns>Nonzero on success or zero with the native error captured on failure.</returns>
    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateDirectoryW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int CreateDirectory(string pathName, nint securityAttributes);

    /// <summary>
    /// Opens or creates one UTF-16 path without shell or ANSI conversion.
    /// </summary>
    /// <param name="fileName">The absolute UTF-16 path.</param>
    /// <param name="desiredAccess">The requested handle access mask.</param>
    /// <param name="shareMode">The allowed concurrent access mask.</param>
    /// <param name="securityAttributes">An unused security-attributes pointer.</param>
    /// <param name="creationDisposition">The requested creation behavior.</param>
    /// <param name="flagsAndAttributes">The file flags and initial attributes.</param>
    /// <param name="templateFile">An unused template handle.</param>
    /// <returns>The opened safe file handle.</returns>
    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    /// <summary>
    /// Queries attributes and the reparse tag from an opened handle.
    /// </summary>
    /// <param name="file">The opened file handle.</param>
    /// <param name="informationClass">The fixed file-attribute-tag information class.</param>
    /// <param name="information">Receives the attribute and tag values.</param>
    /// <param name="bufferSize">The exact output structure size.</param>
    /// <returns>Nonzero on success or zero on failure.</returns>
    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    internal static partial int GetFileInformationByHandleEx(
        SafeFileHandle file,
        int informationClass,
        out FileAttributeTagInformation information,
        uint bufferSize);

    /// <summary>
    /// Atomically replaces one UTF-16 destination path with a prepared source file.
    /// </summary>
    /// <param name="existingFileName">The prepared source path.</param>
    /// <param name="newFileName">The destination path.</param>
    /// <param name="flags">The replace and durability flags.</param>
    /// <returns>Nonzero on success or zero on failure.</returns>
    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "MoveFileExW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int MoveFileEx(string existingFileName, string newFileName, uint flags);

    /// <summary>
    /// Removes one exact UTF-16 temporary path during failed atomic replacement.
    /// </summary>
    /// <param name="fileName">The exact temporary path.</param>
    /// <returns>Nonzero on success or zero on failure.</returns>
    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "DeleteFileW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int DeleteFile(string fileName);

    /// <summary>
    /// Applies deletion disposition to the exact file represented by a handle.
    /// </summary>
    /// <param name="file">The opened file handle.</param>
    /// <param name="informationClass">The fixed file-disposition information class.</param>
    /// <param name="information">The deletion request.</param>
    /// <param name="bufferSize">The exact input structure size.</param>
    /// <returns>Nonzero on success or zero on failure.</returns>
    [LibraryImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    internal static partial int SetFileInformationByHandle(
        SafeFileHandle file,
        int informationClass,
        ref FileDispositionInformation information,
        uint bufferSize);
}
