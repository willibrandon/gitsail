namespace GitSail.Domain;

/// <summary>
/// Identifies the operating-system representation retained by a Git path.
/// </summary>
internal enum NativePathKind
{
    /// <summary>
    /// Identifies an exact non-NUL Unix byte sequence.
    /// </summary>
    UnixBytes,

    /// <summary>
    /// Identifies an exact Windows UTF-16 string.
    /// </summary>
    WindowsUtf16,
}
