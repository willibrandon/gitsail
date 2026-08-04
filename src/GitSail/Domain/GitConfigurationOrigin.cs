namespace GitSail.Domain;

/// <summary>
/// Retains the exact non-NUL origin bytes reported for a Git configuration value.
/// </summary>
internal sealed class GitConfigurationOrigin : IEquatable<GitConfigurationOrigin>
{
    private readonly byte[] _bytes;

    private GitConfigurationOrigin(byte[] bytes)
    {
        _bytes = bytes;
    }

    /// <summary>
    /// Creates an origin from exact bytes reported by Git.
    /// </summary>
    /// <param name="bytes">The nonempty non-NUL origin bytes.</param>
    /// <returns>An origin that owns a copy of the supplied bytes.</returns>
    internal static GitConfigurationOrigin FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Contains((byte)0))
        {
            throw new ArgumentException("A Git configuration origin must be nonempty and cannot contain NUL.", nameof(bytes));
        }

        return new GitConfigurationOrigin(bytes.ToArray());
    }

    /// <summary>
    /// Gets the exact origin bytes.
    /// </summary>
    /// <returns>A read-only span over origin-owned bytes.</returns>
    internal ReadOnlySpan<byte> GetBytes()
        => _bytes;

    /// <inheritdoc />
    public bool Equals(GitConfigurationOrigin? other)
        => other is not null && _bytes.AsSpan().SequenceEqual(other._bytes);

    /// <inheritdoc />
    public override bool Equals(object? obj)
        => obj is GitConfigurationOrigin other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var value in _bytes)
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }
}
