namespace GitSail.Domain;

/// <summary>
/// Represents an exact SHA-1 or SHA-256 Git object identifier.
/// </summary>
internal sealed class ObjectId : IEquatable<ObjectId>, IComparable<ObjectId>
{
    private readonly byte[] _bytes;

    private ObjectId(byte[] bytes)
    {
        _bytes = bytes;
    }

    /// <summary>
    /// Gets the object format implied by the identifier width.
    /// </summary>
    internal RepositoryObjectFormat Format => _bytes.Length == 20
        ? RepositoryObjectFormat.Sha1
        : RepositoryObjectFormat.Sha256;

    /// <summary>
    /// Parses a lowercase or uppercase hexadecimal object identifier.
    /// </summary>
    /// <param name="hex">The 40- or 64-byte ASCII hexadecimal value.</param>
    /// <param name="objectId">The parsed object identifier when successful.</param>
    /// <returns><see langword="true"/> when the input is a supported exact object identifier.</returns>
    internal static bool TryParseHex(ReadOnlySpan<byte> hex, out ObjectId? objectId)
    {
        objectId = null;
        if (hex.Length is not (40 or 64))
        {
            return false;
        }

        var bytes = new byte[hex.Length / 2];
        for (var index = 0; index < bytes.Length; index++)
        {
            if (!TryParseNibble(hex[index * 2], out var high) ||
                !TryParseNibble(hex[(index * 2) + 1], out var low))
            {
                return false;
            }

            bytes[index] = (byte)((high << 4) | low);
        }

        objectId = new ObjectId(bytes);
        return true;
    }

    /// <summary>
    /// Gets the exact binary object identifier.
    /// </summary>
    /// <returns>A read-only span over identifier-owned bytes.</returns>
    internal ReadOnlySpan<byte> GetBytes()
        => _bytes;

    /// <inheritdoc />
    public bool Equals(ObjectId? other)
        => other is not null && _bytes.AsSpan().SequenceEqual(other._bytes);

    /// <inheritdoc />
    public override bool Equals(object? obj)
        => obj is ObjectId other && Equals(other);

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
    public int CompareTo(ObjectId? other)
        => other is null ? 1 : _bytes.AsSpan().SequenceCompareTo(other._bytes);

    /// <inheritdoc />
    public override string ToString()
    {
        return string.Create(_bytes.Length * 2, _bytes, static (destination, bytes) =>
        {
            for (var index = 0; index < bytes.Length; index++)
            {
                var value = bytes[index];
                destination[index * 2] = GetHexCharacter(value >> 4);
                destination[(index * 2) + 1] = GetHexCharacter(value & 0x0f);
            }
        });
    }

    private static bool TryParseNibble(byte value, out int nibble)
    {
        if (value is >= (byte)'0' and <= (byte)'9')
        {
            nibble = value - (byte)'0';
            return true;
        }

        if (value is >= (byte)'a' and <= (byte)'f')
        {
            nibble = value - (byte)'a' + 10;
            return true;
        }

        if (value is >= (byte)'A' and <= (byte)'F')
        {
            nibble = value - (byte)'A' + 10;
            return true;
        }

        nibble = 0;
        return false;
    }

    private static char GetHexCharacter(int value)
        => "0123456789abcdef"[value];
}
