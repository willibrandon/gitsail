namespace GitSail.Domain;

/// <summary>
/// Represents one lossless path record from Git porcelain version 2 status output.
/// </summary>
/// <param name="Kind">The porcelain record shape.</param>
/// <param name="IndexStatus">The index-side status.</param>
/// <param name="WorkTreeStatus">The worktree-side status.</param>
/// <param name="Path">The current or destination path.</param>
/// <param name="OriginalPath">The source path for a rename or copy.</param>
/// <param name="SimilarityPercentage">The rename or copy similarity percentage.</param>
/// <param name="IsSubmodule">Whether Git identified the entry as a submodule.</param>
internal sealed record RepositoryStatusEntry(
    RepositoryStatusEntryKind Kind,
    GitFileStatus IndexStatus,
    GitFileStatus WorkTreeStatus,
    GitPath Path,
    GitPath? OriginalPath,
    int? SimilarityPercentage,
    bool IsSubmodule);
