namespace GitSail.Domain;

/// <summary>
/// Retains the exact bytes of one canonical Git configuration key.
/// </summary>
internal sealed class GitConfigurationKey : IEquatable<GitConfigurationKey>
{
    private readonly byte[] _bytes;

    private GitConfigurationKey(byte[] bytes)
    {
        _bytes = bytes;
    }

    /// <summary>
    /// Gets a control-sanitized representation intended only for display.
    /// </summary>
    internal string DisplayText => GitPath.FromUnixBytes(_bytes).DisplayText;

    /// <summary>
    /// Creates a key from the exact canonical bytes reported by Git.
    /// </summary>
    /// <param name="bytes">The nonempty canonical key bytes.</param>
    /// <returns>A key that owns a copy of the supplied bytes.</returns>
    internal static GitConfigurationKey FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (!IsValid(bytes))
        {
            throw new ArgumentException("A Git configuration key has an invalid representation.", nameof(bytes));
        }

        return new GitConfigurationKey(bytes.ToArray());
    }

    /// <summary>
    /// Gets the exact canonical bytes retained by this key.
    /// </summary>
    /// <returns>A read-only span over key-owned bytes.</returns>
    internal ReadOnlySpan<byte> GetBytes()
        => _bytes;

    /// <inheritdoc />
    public bool Equals(GitConfigurationKey? other)
        => other is not null && _bytes.AsSpan().SequenceEqual(other._bytes);

    /// <inheritdoc />
    public override bool Equals(object? obj)
        => obj is GitConfigurationKey other && Equals(other);

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

    /// <inheritdoc />
    public override string ToString()
        => DisplayText;

    private static bool IsValid(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes[0] == (byte)'.' || bytes[^1] == (byte)'.' || !bytes.Contains((byte)'.'))
        {
            return false;
        }

        foreach (var value in bytes)
        {
            if (value is <= 0x20 or 0x7f)
            {
                return false;
            }
        }

        return true;
    }
}
