using System.Globalization;

namespace GitSail.Domain;

/// <summary>
/// Describes one exact ordered stash reflog entry prepared for user action.
/// </summary>
internal sealed class StashInfo
{
    private readonly byte[] _messageBytes;

    /// <summary>
    /// Initializes one immutable exact stash entry.
    /// </summary>
    /// <param name="index">The zero-based current stash reflog position.</param>
    /// <param name="objectId">The exact stash commit object identifier.</param>
    /// <param name="messageBytes">The exact reflog subject bytes.</param>
    /// <param name="createdAt">The reflog entry timestamp reported by Git.</param>
    internal StashInfo(
        int index,
        ObjectId objectId,
        ReadOnlySpan<byte> messageBytes,
        DateTimeOffset createdAt)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentNullException.ThrowIfNull(objectId);
        if (messageBytes.Contains((byte)0))
        {
            throw new ArgumentException("A stash message cannot contain NUL.", nameof(messageBytes));
        }

        Index = index;
        ObjectId = objectId;
        _messageBytes = messageBytes.ToArray();
        CreatedAt = createdAt;
    }

    /// <summary>
    /// Gets the zero-based current stash reflog position.
    /// </summary>
    internal int Index { get; }

    /// <summary>
    /// Gets the exact stash commit object identifier.
    /// </summary>
    internal ObjectId ObjectId { get; }

    /// <summary>
    /// Gets the generated selector accepted by stash pop and drop.
    /// </summary>
    internal string Selector => string.Create(
        CultureInfo.InvariantCulture,
        $"stash@{{{Index}}}");

    /// <summary>
    /// Gets the exact reflog subject bytes.
    /// </summary>
    internal ReadOnlyMemory<byte> MessageBytes => _messageBytes;

    /// <summary>
    /// Gets a control-safe representation of the reflog subject.
    /// </summary>
    internal string DisplayMessage => _messageBytes.Length == 0
        ? "(no message)"
        : GitPath.FromUnixBytes(_messageBytes).DisplayText;

    /// <summary>
    /// Gets the reflog entry timestamp reported by Git.
    /// </summary>
    internal DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// Determines whether another entry has the same action-relevant identity.
    /// </summary>
    /// <param name="other">The independently captured stash entry.</param>
    /// <returns><see langword="true"/> when every retained field is identical.</returns>
    internal bool Matches(StashInfo other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Index == other.Index &&
            ObjectId.Equals(other.ObjectId) &&
            _messageBytes.AsSpan().SequenceEqual(other._messageBytes) &&
            CreatedAt.Equals(other.CreatedAt);
    }
}
