namespace GitSail.Domain;

/// <summary>
/// Selects how a local source repository supplies objects to a clone.
/// </summary>
internal enum RepositoryCloneMode
{
    /// <summary>
    /// Uses Git's normal behavior, including local hard-link optimization when available.
    /// </summary>
    Standard,

    /// <summary>
    /// Copies local object files instead of hard-linking them.
    /// </summary>
    FullCopy,

    /// <summary>
    /// Borrows local objects through an alternates file and therefore depends on the source repository.
    /// </summary>
    Shared,
}
