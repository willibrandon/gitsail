namespace GitSail.Domain;

/// <summary>
/// Retains an exact non-NUL Git remote name independently from display text.
/// </summary>
internal sealed class RemoteName : IEquatable<RemoteName>, IComparable<RemoteName>
{
    private readonly byte[] _bytes;

    private RemoteName(byte[] bytes)
    {
        _bytes = bytes;
    }

    /// <summary>
    /// Gets a control-sanitized representation intended only for display.
    /// </summary>
    internal string DisplayText => GitPath.FromUnixBytes(_bytes).DisplayText;

    /// <summary>
    /// Creates a remote name from exact bytes reported or validated by Git.
    /// </summary>
    /// <param name="bytes">The nonempty non-NUL remote-name bytes.</param>
    /// <returns>A remote name that owns a copy of the supplied bytes.</returns>
    internal static RemoteName FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            throw new ArgumentException("A Git remote name cannot be empty.", nameof(bytes));
        }

        if (bytes.Contains((byte)0))
        {
            throw new ArgumentException("A Git remote name cannot contain NUL.", nameof(bytes));
        }

        return new RemoteName(bytes.ToArray());
    }

    /// <summary>
    /// Gets the exact bytes retained by this remote name.
    /// </summary>
    /// <returns>A read-only span over name-owned bytes.</returns>
    internal ReadOnlySpan<byte> GetBytes()
        => _bytes;

    /// <inheritdoc />
    public bool Equals(RemoteName? other)
        => other is not null && _bytes.AsSpan().SequenceEqual(other._bytes);

    /// <inheritdoc />
    public override bool Equals(object? obj)
        => obj is RemoteName other && Equals(other);

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
    public int CompareTo(RemoteName? other)
        => other is null ? 1 : _bytes.AsSpan().SequenceCompareTo(other._bytes);

    /// <inheritdoc />
    public override string ToString()
        => DisplayText;
}
