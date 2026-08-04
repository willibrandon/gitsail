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

    /// <summary>
    /// Finds the first hunk whose header follows one presentation line.
    /// </summary>
    /// <param name="lineNumber">The one-based editor presentation line.</param>
    /// <returns>The following hunk, or <see langword="null"/> after the final hunk.</returns>
    internal RawPatchHunk? FindNextHunk(int lineNumber)
        => Hunks.FirstOrDefault(hunk => hunk.StartLineNumber > lineNumber);

    /// <summary>
    /// Finds the last hunk whose header precedes one presentation line.
    /// </summary>
    /// <param name="lineNumber">The one-based editor presentation line.</param>
    /// <returns>The preceding or containing hunk, or <see langword="null"/> before the first hunk.</returns>
    internal RawPatchHunk? FindPreviousHunk(int lineNumber)
    {
        for (var index = Hunks.Length - 1; index >= 0; index--)
        {
            if (Hunks[index].StartLineNumber < lineNumber)
            {
                return Hunks[index];
            }
        }

        return null;
    }
}
