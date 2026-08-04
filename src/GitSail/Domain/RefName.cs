namespace GitSail.Domain;

/// <summary>
/// Retains an exact non-NUL Git reference name independently from display text.
/// </summary>
internal sealed class RefName : IEquatable<RefName>, IComparable<RefName>
{
    private readonly byte[] _bytes;

    private RefName(byte[] bytes)
    {
        _bytes = bytes;
    }

    /// <summary>
    /// Gets a control-sanitized representation intended only for display.
    /// </summary>
    internal string DisplayText => GitPath.FromUnixBytes(_bytes).DisplayText;

    /// <summary>
    /// Creates a reference name from exact bytes reported by Git.
    /// </summary>
    /// <param name="bytes">The nonempty non-NUL reference bytes.</param>
    /// <returns>A reference name that owns a copy of the supplied bytes.</returns>
    internal static RefName FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            throw new ArgumentException("A Git reference name cannot be empty.", nameof(bytes));
        }

        if (bytes.Contains((byte)0))
        {
            throw new ArgumentException("A Git reference name cannot contain NUL.", nameof(bytes));
        }

        return new RefName(bytes.ToArray());
    }

    /// <summary>
    /// Gets the exact bytes retained by this reference name.
    /// </summary>
    /// <returns>A read-only span over reference-owned bytes.</returns>
    internal ReadOnlySpan<byte> GetBytes()
        => _bytes;

    /// <inheritdoc />
    public bool Equals(RefName? other)
        => other is not null && _bytes.AsSpan().SequenceEqual(other._bytes);

    /// <inheritdoc />
    public override bool Equals(object? obj)
        => obj is RefName other && Equals(other);

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
    public int CompareTo(RefName? other)
        => other is null ? 1 : _bytes.AsSpan().SequenceCompareTo(other._bytes);

    /// <inheritdoc />
    public override string ToString()
        => DisplayText;
}
