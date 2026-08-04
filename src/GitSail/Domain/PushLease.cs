using System.Text;

namespace GitSail.Domain;

/// <summary>
/// Retains one exact force-with-lease option bound to a destination and expected OID.
/// </summary>
internal sealed class PushLease
{
    private readonly byte[] _bytes;

    /// <summary>
    /// Initializes one explicit lease, using an empty expectation when the destination must be absent.
    /// </summary>
    /// <param name="destination">The exact fully qualified destination ref.</param>
    /// <param name="expectedObjectId">The expected current remote OID, or no OID for an absent ref.</param>
    internal PushLease(RefName destination, ObjectId? expectedObjectId)
    {
        ArgumentNullException.ThrowIfNull(destination);
        Destination = destination;
        ExpectedObjectId = expectedObjectId;
        var prefix = "--force-with-lease="u8;
        var expected = expectedObjectId is null
            ? ReadOnlySpan<byte>.Empty
            : Encoding.ASCII.GetBytes(expectedObjectId.ToString());
        _bytes = new byte[prefix.Length + destination.GetBytes().Length + 1 + expected.Length];
        prefix.CopyTo(_bytes);
        destination.GetBytes().CopyTo(_bytes.AsSpan(prefix.Length));
        var separator = prefix.Length + destination.GetBytes().Length;
        _bytes[separator] = (byte)':';
        expected.CopyTo(_bytes.AsSpan(separator + 1));
    }

    /// <summary>
    /// Gets the exact protected destination ref.
    /// </summary>
    internal RefName Destination { get; }

    /// <summary>
    /// Gets the exact expected current OID, or no OID when the ref must be absent.
    /// </summary>
    internal ObjectId? ExpectedObjectId { get; }

    /// <summary>
    /// Gets the complete exact option bytes passed to Git.
    /// </summary>
    /// <returns>A read-only span over lease-owned bytes.</returns>
    internal ReadOnlySpan<byte> GetBytes()
        => _bytes;
}
