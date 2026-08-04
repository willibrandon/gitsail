using GitSail.Git.Execution;

namespace GitSail.Domain;

/// <summary>
/// Owns one immutable raw diff spool together with its generation-stamped file index.
/// </summary>
internal sealed class RawDiffDocument : IDisposable
{
    private readonly RawByteSpool _spool;

    /// <summary>
    /// Initializes ownership of a complete spool and its validated index.
    /// </summary>
    /// <param name="spool">The complete exact-byte spool.</param>
    /// <param name="index">The nested exact byte index into the spool.</param>
    internal RawDiffDocument(RawByteSpool spool, RawDiffIndex index)
    {
        ArgumentNullException.ThrowIfNull(spool);
        ArgumentNullException.ThrowIfNull(index);
        _spool = spool;
        Index = index;
    }

    /// <summary>
    /// Gets the immutable nested byte index for this document.
    /// </summary>
    internal RawDiffIndex Index { get; }

    /// <summary>
    /// Reads the exact bytes for one indexed file patch.
    /// </summary>
    /// <param name="file">A file patch contained by this document's index.</param>
    /// <param name="cancellationToken">Signals slice-read cancellation.</param>
    /// <returns>The exact indexed patch bytes.</returns>
    internal Task<byte[]> ReadFileAsync(
        RawDiffFile file,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!Index.Files.Contains(file))
        {
            throw new ArgumentException("The raw diff file does not belong to this document.", nameof(file));
        }

        return _spool.ReadSliceAsync(
            file.Offset,
            checked((int)file.Length),
            cancellationToken);
    }

    /// <summary>
    /// Reads a bounded exact prefix of one indexed file patch for presentation.
    /// </summary>
    /// <param name="file">A file patch contained by this document's index.</param>
    /// <param name="maximumBytes">The positive maximum byte count to return.</param>
    /// <param name="cancellationToken">Signals prefix-read cancellation.</param>
    /// <returns>The exact patch prefix, up to the requested maximum.</returns>
    internal Task<byte[]> ReadFilePrefixAsync(
        RawDiffFile file,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if (!Index.Files.Contains(file))
        {
            throw new ArgumentException("The raw diff file does not belong to this document.", nameof(file));
        }

        return _spool.ReadSliceAsync(
            file.Offset,
            checked((int)Math.Min(file.Length, maximumBytes)),
            cancellationToken);
    }

    /// <summary>
    /// Reads an applicable exact patch containing the original file header and one indexed complete hunk.
    /// </summary>
    /// <param name="file">A file patch contained by this document's index.</param>
    /// <param name="hunk">A complete hunk contained by the file's patch index.</param>
    /// <param name="cancellationToken">Signals exact slice-read cancellation.</param>
    /// <returns>The newly owned header and hunk bytes without presentation decoding.</returns>
    internal async Task<byte[]> ReadHunkPatchAsync(
        RawDiffFile file,
        RawPatchHunk hunk,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(hunk);
        if (!Index.Files.Contains(file))
        {
            throw new ArgumentException("The raw diff file does not belong to this document.", nameof(file));
        }

        if (!file.PatchIndex.Hunks.Contains(hunk))
        {
            throw new ArgumentException("The raw patch hunk does not belong to this file.", nameof(hunk));
        }

        var result = new byte[checked(file.PatchIndex.HeaderLength + hunk.Length)];
        await _spool.ReadSliceAsync(
            file.Offset,
            result.AsMemory(0, file.PatchIndex.HeaderLength),
            cancellationToken).ConfigureAwait(false);
        await _spool.ReadSliceAsync(
            checked(file.Offset + hunk.Offset),
            result.AsMemory(file.PatchIndex.HeaderLength, hunk.Length),
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Closes the underlying spool and removes any temporary file it owns.
    /// </summary>
    public void Dispose()
        => _spool.Dispose();
}
