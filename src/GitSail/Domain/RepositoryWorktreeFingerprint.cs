namespace GitSail.Domain;

/// <summary>
/// Retains a SHA-256 identity of exact tracked, untracked, and ignored worktree state.
/// </summary>
internal sealed class RepositoryWorktreeFingerprint
{
    private const int Sha256Bytes = 32;
    private readonly byte[] _bytes;

    /// <summary>
    /// Initializes one immutable worktree fingerprint.
    /// </summary>
    /// <param name="bytes">The exact 32-byte SHA-256 digest.</param>
    internal RepositoryWorktreeFingerprint(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Sha256Bytes)
        {
            throw new ArgumentException("A worktree fingerprint must contain exactly 32 bytes.", nameof(bytes));
        }

        _bytes = bytes.ToArray();
    }

    /// <summary>
    /// Gets the exact immutable SHA-256 digest.
    /// </summary>
    internal ReadOnlyMemory<byte> Bytes => _bytes;

    /// <summary>
    /// Determines whether another capture identifies the same complete worktree state.
    /// </summary>
    /// <param name="other">The independently captured worktree fingerprint.</param>
    /// <returns><see langword="true"/> when both SHA-256 digests match exactly.</returns>
    internal bool Matches(RepositoryWorktreeFingerprint other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return _bytes.AsSpan().SequenceEqual(other._bytes);
    }
}
