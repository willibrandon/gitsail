using System.Collections.Immutable;

namespace GitSail.Domain;

/// <summary>
/// Contains file-level offsets into one immutable raw diff generation.
/// </summary>
/// <param name="Generation">The repository operation generation that produced the diff.</param>
/// <param name="Files">The ordered file-patch index.</param>
internal sealed record RawDiffIndex(
    OperationGeneration Generation,
    ImmutableArray<RawDiffFile> Files)
{
    /// <summary>
    /// Finds a patch whose old or new side has the supplied exact path identity.
    /// </summary>
    /// <param name="path">The exact status path to locate.</param>
    /// <returns>The matching indexed patch, or <see langword="null"/> when absent.</returns>
    internal RawDiffFile? Find(GitPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Files.FirstOrDefault(file => file.OldPath.Equals(path) || file.NewPath.Equals(path));
    }
}
