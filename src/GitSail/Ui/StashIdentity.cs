using GitSail.Domain;

namespace GitSail.Ui;

/// <summary>
/// Identifies one exact stash object at one current reflog position.
/// </summary>
internal sealed class StashIdentity : IEquatable<StashIdentity>
{
    /// <summary>
    /// Initializes one exact stash list identity.
    /// </summary>
    /// <param name="index">The current zero-based stash reflog position.</param>
    /// <param name="objectId">The exact stash commit object identifier.</param>
    internal StashIdentity(int index, ObjectId objectId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentNullException.ThrowIfNull(objectId);
        Index = index;
        ObjectId = objectId;
    }

    /// <summary>
    /// Gets the current zero-based stash reflog position.
    /// </summary>
    internal int Index { get; }

    /// <summary>
    /// Gets the exact stash commit object identifier.
    /// </summary>
    internal ObjectId ObjectId { get; }

    /// <inheritdoc />
    public bool Equals(StashIdentity? other)
        => other is not null && Index == other.Index && ObjectId.Equals(other.ObjectId);

    /// <inheritdoc />
    public override bool Equals(object? obj)
        => obj is StashIdentity other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(Index, ObjectId);
}
