namespace GitSail.Domain;

/// <summary>
/// Describes the exact detached HEAD commit for which commit confirmation is required.
/// </summary>
internal sealed record DetachedHeadWarning
{
    /// <summary>
    /// Initializes one warning for an exact detached HEAD commit.
    /// </summary>
    /// <param name="headObjectId">The exact detached HEAD commit that the user must confirm.</param>
    internal DetachedHeadWarning(ObjectId headObjectId)
    {
        ArgumentNullException.ThrowIfNull(headObjectId);
        HeadObjectId = headObjectId;
    }

    /// <summary>
    /// Gets the exact detached HEAD commit that would receive the new commit.
    /// </summary>
    internal ObjectId HeadObjectId { get; }

    /// <summary>
    /// Determines whether another warning identifies the same exact detached HEAD commit.
    /// </summary>
    /// <param name="other">The independently captured warning to compare.</param>
    /// <returns><see langword="true"/> only when both warnings identify the same commit.</returns>
    internal bool Matches(DetachedHeadWarning? other)
        => other is not null && Equals(HeadObjectId, other.HeadObjectId);
}
