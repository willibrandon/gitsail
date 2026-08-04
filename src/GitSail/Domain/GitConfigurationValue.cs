namespace GitSail.Domain;

/// <summary>
/// Retains the exact bytes of one explicit Git configuration value.
/// </summary>
internal sealed class GitConfigurationValue : IEquatable<GitConfigurationValue>
{
    private readonly byte[] _bytes;

    private GitConfigurationValue(byte[] bytes)
    {
        _bytes = bytes;
    }

    /// <summary>
    /// Gets whether the configuration value is explicitly empty.
    /// </summary>
    internal bool IsEmpty => _bytes.Length == 0;

    /// <summary>
    /// Creates a configuration value from exact bytes reported by Git.
    /// </summary>
    /// <param name="bytes">The value bytes, which may be empty or contain newlines.</param>
    /// <returns>A value that owns a copy of the supplied bytes.</returns>
    internal static GitConfigurationValue FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Contains((byte)0))
        {
            throw new ArgumentException("A Git configuration value cannot contain NUL.", nameof(bytes));
        }

        return new GitConfigurationValue(bytes.ToArray());
    }

    /// <summary>
    /// Gets the exact configuration value bytes.
    /// </summary>
    /// <returns>A read-only span over value-owned bytes.</returns>
    internal ReadOnlySpan<byte> GetBytes()
        => _bytes;

    /// <inheritdoc />
    public bool Equals(GitConfigurationValue? other)
        => other is not null && _bytes.AsSpan().SequenceEqual(other._bytes);

    /// <inheritdoc />
    public override bool Equals(object? obj)
        => obj is GitConfigurationValue other && Equals(other);

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
