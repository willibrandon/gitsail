using GitSail.Domain;

namespace GitSail.Git.Parsing;

/// <summary>
/// Builds an applicable exact-byte file patch from an original header and selected complete hunks.
/// </summary>
internal static class RawPatchSelectionBuilder
{
    /// <summary>
    /// Copies one complete original hunk behind the unchanged original file header.
    /// </summary>
    /// <param name="patch">The complete exact original file patch.</param>
    /// <param name="index">The validated byte index for the patch.</param>
    /// <param name="hunk">The selected hunk owned by the index.</param>
    /// <returns>A new applicable patch containing only the selected hunk.</returns>
    internal static byte[] BuildSingleHunk(
        ReadOnlySpan<byte> patch,
        RawPatchIndex index,
        RawPatchHunk hunk)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(hunk);
        if (!index.Hunks.Contains(hunk))
        {
            throw new ArgumentException("The selected hunk does not belong to this patch index.", nameof(hunk));
        }

        if (index.HeaderLength < 0 || index.HeaderLength > patch.Length ||
            hunk.Offset < index.HeaderLength || hunk.Length > patch.Length - hunk.Offset)
        {
            throw new InvalidDataException("The raw patch index contained an out-of-range slice.");
        }

        var result = new byte[checked(index.HeaderLength + hunk.Length)];
        patch[..index.HeaderLength].CopyTo(result);
        patch.Slice(hunk.Offset, hunk.Length).CopyTo(result.AsSpan(index.HeaderLength));
        return result;
    }
}
