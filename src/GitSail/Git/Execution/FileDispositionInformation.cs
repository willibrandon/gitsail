namespace GitSail.Git.Execution;

/// <summary>
/// Requests deletion of the exact regular file opened by a Windows handle.
/// </summary>
internal struct FileDispositionInformation
{
    /// <summary>
    /// Contains the Win32 Boolean value that requests deletion on handle close.
    /// </summary>
    internal byte _deleteFile;
}
