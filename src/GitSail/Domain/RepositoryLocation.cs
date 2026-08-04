namespace GitSail.Domain;

/// <summary>
/// Contains the canonical locations and storage format discovered through Git.
/// </summary>
/// <param name="GitDirectory">The canonical per-worktree Git directory.</param>
/// <param name="CommonDirectory">The canonical common Git directory.</param>
/// <param name="WorkTree">The canonical worktree root, or <see langword="null"/> for a bare repository.</param>
/// <param name="Prefix">The path from the worktree root to the discovery directory, or <see langword="null"/> at the root.</param>
/// <param name="ObjectFormat">The repository object identifier algorithm.</param>
/// <param name="IsBare">Whether the repository is bare.</param>
internal sealed record RepositoryLocation(
    GitPath GitDirectory,
    GitPath CommonDirectory,
    GitPath? WorkTree,
    GitPath? Prefix,
    RepositoryObjectFormat ObjectFormat,
    bool IsBare);
