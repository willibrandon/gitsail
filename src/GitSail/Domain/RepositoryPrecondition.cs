namespace GitSail.Domain;

/// <summary>
/// Captures the live HEAD object, symbolic attachment, and exact index fingerprint guarding a mutation.
/// </summary>
internal sealed class RepositoryPrecondition
{
    private const int Sha256Bytes = 32;
    private readonly byte[] _indexFingerprint;

    /// <summary>
    /// Initializes one immutable repository mutation precondition from independently owned values.
    /// </summary>
    /// <param name="headObjectId">The live HEAD object, or <see langword="null"/> for an unborn repository.</param>
    /// <param name="headName">The exact symbolic HEAD target, or <see langword="null"/> when detached.</param>
    /// <param name="indexFingerprint">The SHA-256 fingerprint of Git's exact staged-entry stream.</param>
    internal RepositoryPrecondition(
        ObjectId? headObjectId,
        RefName? headName,
        ReadOnlySpan<byte> indexFingerprint)
    {
        if (indexFingerprint.Length != Sha256Bytes)
        {
            throw new ArgumentException("An index fingerprint must contain exactly 32 bytes.", nameof(indexFingerprint));
        }

        HeadObjectId = headObjectId;
        HeadName = headName;
        _indexFingerprint = indexFingerprint.ToArray();
    }

    /// <summary>
    /// Gets the live HEAD object observed with the index fingerprint.
    /// </summary>
    internal ObjectId? HeadObjectId { get; }

    /// <summary>
    /// Gets the exact symbolic HEAD target, or <see langword="null"/> when HEAD is detached.
    /// </summary>
    internal RefName? HeadName { get; }

    /// <summary>
    /// Gets the immutable SHA-256 fingerprint of Git's exact staged-entry stream.
    /// </summary>
    internal ReadOnlyMemory<byte> IndexFingerprint => _indexFingerprint;

    /// <summary>
    /// Determines whether another capture has the same HEAD object, attachment, and staged contents.
    /// </summary>
    /// <param name="other">The independently captured live repository precondition.</param>
    /// <returns><see langword="true"/> only when every guarded repository value matches exactly.</returns>
    internal bool Matches(RepositoryPrecondition other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Equals(HeadObjectId, other.HeadObjectId) &&
            Equals(HeadName, other.HeadName) &&
            _indexFingerprint.AsSpan().SequenceEqual(other._indexFingerprint);
    }

    /// <summary>
    /// Determines whether a porcelain branch name represents this exact symbolic HEAD target.
    /// </summary>
    /// <param name="statusName">The short local branch name reported by porcelain status.</param>
    /// <returns><see langword="true"/> only when detached or attached state and branch bytes agree.</returns>
    internal bool MatchesStatusHeadName(RefName? statusName)
    {
        if (HeadName is null)
        {
            return statusName is null;
        }

        if (statusName is null)
        {
            return false;
        }

        ReadOnlySpan<byte> localPrefix = "refs/heads/"u8;
        var symbolicBytes = HeadName.GetBytes();
        return symbolicBytes.StartsWith(localPrefix) &&
            symbolicBytes[localPrefix.Length..].SequenceEqual(statusName.GetBytes());
    }
}
