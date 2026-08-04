namespace GitSail.Domain;

/// <summary>
/// Identifies the object identifier algorithm used by a Git repository.
/// </summary>
internal enum RepositoryObjectFormat
{
    /// <summary>
    /// Identifies the 160-bit SHA-1 object format.
    /// </summary>
    Sha1,

    /// <summary>
    /// Identifies the 256-bit SHA-256 object format.
    /// </summary>
    Sha256,
}
