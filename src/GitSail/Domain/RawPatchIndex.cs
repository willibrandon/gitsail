using System.Collections.Immutable;

namespace GitSail.Domain;

/// <summary>
/// Contains exact file-header and validated hunk slices for one raw file patch.
/// </summary>
/// <param name="HeaderLength">The bytes preceding the first hunk, or the complete patch length without hunks.</param>
/// <param name="Hunks">The ordered validated unified-hunk index.</param>
internal sealed record RawPatchIndex(
    int HeaderLength,
    ImmutableArray<RawPatchHunk> Hunks)
{
    /// <summary>
    /// Finds the hunk containing one presentation line number.
    /// </summary>
    /// <param name="lineNumber">The one-based editor presentation line.</param>
    /// <returns>The containing hunk, or <see langword="null"/> outside all hunks.</returns>
    internal RawPatchHunk? FindHunkAtLine(int lineNumber)
        => Hunks.FirstOrDefault(hunk =>
            lineNumber >= hunk.StartLineNumber && lineNumber <= hunk.EndLineNumber);
}
