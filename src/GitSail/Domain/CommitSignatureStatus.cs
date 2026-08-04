namespace GitSail.Domain;

/// <summary>
/// Identifies Git's machine-readable signature status for one commit.
/// </summary>
internal enum CommitSignatureStatus
{
    /// <summary>
    /// The commit has no signature.
    /// </summary>
    None,

    /// <summary>
    /// The commit has a valid signature from a trusted key.
    /// </summary>
    Good,

    /// <summary>
    /// The commit has a bad signature.
    /// </summary>
    Bad,

    /// <summary>
    /// The commit has a valid signature whose trust is unknown.
    /// </summary>
    UnknownValidity,

    /// <summary>
    /// The commit has a valid signature that has expired.
    /// </summary>
    ExpiredSignature,

    /// <summary>
    /// The commit has a valid signature made by an expired key.
    /// </summary>
    ExpiredKey,

    /// <summary>
    /// The commit has a valid signature made by a revoked key.
    /// </summary>
    RevokedKey,

    /// <summary>
    /// Git could not check the commit signature.
    /// </summary>
    CannotCheck,
}
