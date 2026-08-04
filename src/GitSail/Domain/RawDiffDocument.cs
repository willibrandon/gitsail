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
    /// <param name="index">The file-level index into the spool.</param>
    internal RawDiffDocument(RawByteSpool spool, RawDiffIndex index)
    {
        ArgumentNullException.ThrowIfNull(spool);
        ArgumentNullException.ThrowIfNull(index);
        _spool = spool;
        Index = index;
    }

    /// <summary>
    /// Gets the immutable file-level index for this document.
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
    /// Closes the underlying spool and removes any temporary file it owns.
    /// </summary>
    public void Dispose()
        => _spool.Dispose();
}
