namespace GitSail.Domain;

/// <summary>
/// Captures the live HEAD identity and exact index-content fingerprint guarding a repository mutation.
/// </summary>
internal sealed class RepositoryPrecondition
{
    private const int Sha256Bytes = 32;
    private readonly byte[] _indexFingerprint;

    /// <summary>
    /// Initializes one immutable repository mutation precondition from independently owned values.
    /// </summary>
    /// <param name="headObjectId">The live HEAD object, or <see langword="null"/> for an unborn repository.</param>
    /// <param name="indexFingerprint">The SHA-256 fingerprint of Git's exact staged-entry stream.</param>
    internal RepositoryPrecondition(ObjectId? headObjectId, ReadOnlySpan<byte> indexFingerprint)
    {
        if (indexFingerprint.Length != Sha256Bytes)
        {
            throw new ArgumentException("An index fingerprint must contain exactly 32 bytes.", nameof(indexFingerprint));
        }

        HeadObjectId = headObjectId;
        _indexFingerprint = indexFingerprint.ToArray();
    }

    /// <summary>
    /// Gets the live HEAD object observed with the index fingerprint.
    /// </summary>
    internal ObjectId? HeadObjectId { get; }

    /// <summary>
    /// Gets the immutable SHA-256 fingerprint of Git's exact staged-entry stream.
    /// </summary>
    internal ReadOnlyMemory<byte> IndexFingerprint => _indexFingerprint;

    /// <summary>
    /// Determines whether another live capture has the same HEAD and staged index contents.
    /// </summary>
    /// <param name="other">The independently captured live repository precondition.</param>
    /// <returns><see langword="true"/> only when both guarded repository values match exactly.</returns>
    internal bool Matches(RepositoryPrecondition other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Equals(HeadObjectId, other.HeadObjectId) &&
            _indexFingerprint.AsSpan().SequenceEqual(other._indexFingerprint);
    }
}
