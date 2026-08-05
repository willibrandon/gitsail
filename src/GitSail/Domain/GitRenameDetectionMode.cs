namespace GitSail.Domain;

/// <summary>
/// Identifies the explicit rename and copy detection behavior used by status and diff commands.
/// </summary>
internal enum GitRenameDetectionMode
{
    /// <summary>
    /// Disables rename and copy detection.
    /// </summary>
    Disabled,

    /// <summary>
    /// Detects renames without detecting copies.
    /// </summary>
    Renames,

    /// <summary>
    /// Detects both renames and copies.
    /// </summary>
    Copies,
}
