using System.Runtime.InteropServices;

namespace GitSail.Git.Execution;

/// <summary>
/// Receives Windows file attributes and a reparse tag from an opened handle.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FileAttributeTagInformation
{
    /// <summary>
    /// Contains the opened file's Windows attribute bit field.
    /// </summary>
    internal uint _fileAttributes;

    /// <summary>
    /// Contains the reparse tag when the reparse-point attribute is present.
    /// </summary>
    internal uint _reparseTag;
}
