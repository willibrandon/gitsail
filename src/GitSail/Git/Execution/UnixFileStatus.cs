using System.Runtime.InteropServices;

namespace GitSail.Git.Execution;

/// <summary>
/// Represents the stable file-status layout returned by the .NET runtime Unix portability layer.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 120)]
internal struct UnixFileStatus
{
    /// <summary>
    /// Contains the portable Unix file type and permission bits.
    /// </summary>
    [FieldOffset(4)]
    internal int Mode;

    /// <summary>
    /// Contains the portable device identifier.
    /// </summary>
    [FieldOffset(88)]
    internal long Device;

    /// <summary>
    /// Contains the portable inode identifier.
    /// </summary>
    [FieldOffset(104)]
    internal long Inode;
}
