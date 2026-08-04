namespace GitSail.Domain;

/// <summary>
/// Retains one exact fully qualified source-to-destination push mapping.
/// </summary>
internal sealed class PushRefSpec : IEquatable<PushRefSpec>
{
    private readonly byte[] _bytes;

    /// <summary>
    /// Initializes one exact push mapping, including an optional empty deletion source.
    /// </summary>
    /// <param name="source">The fully qualified source ref, or <see langword="null"/> for deletion.</param>
    /// <param name="destination">The fully qualified destination ref.</param>
    internal PushRefSpec(RefName? source, RefName destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        Source = source;
        Destination = destination;
        var sourceLength = source?.GetBytes().Length ?? 0;
        _bytes = new byte[sourceLength + 1 + destination.GetBytes().Length];
        source?.GetBytes().CopyTo(_bytes);
        _bytes[sourceLength] = (byte)':';
        destination.GetBytes().CopyTo(_bytes.AsSpan(sourceLength + 1));
    }

    /// <summary>
    /// Gets the exact fully qualified source ref, or no source for deletion.
    /// </summary>
    internal RefName? Source { get; }

    /// <summary>
    /// Gets the exact fully qualified remote destination ref.
    /// </summary>
    internal RefName Destination { get; }

    /// <summary>
    /// Gets the exact non-forcing refspec bytes passed back to Git.
    /// </summary>
    /// <returns>A read-only span over refspec-owned bytes.</returns>
    internal ReadOnlySpan<byte> GetBytes()
        => _bytes;

    /// <inheritdoc />
    public bool Equals(PushRefSpec? other)
        => other is not null && _bytes.AsSpan().SequenceEqual(other._bytes);

    /// <inheritdoc />
    public override bool Equals(object? obj)
        => obj is PushRefSpec other && Equals(other);

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
        => GitPath.FromUnixBytes(_bytes).DisplayText;
}
