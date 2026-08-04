namespace GitSail.Domain;

/// <summary>
/// Describes one exact destination URL's advertised state for a planned ref update.
/// </summary>
internal sealed class PushDestinationExpectation
{
    /// <summary>
    /// Initializes one immutable advertised destination expectation and commit relationship.
    /// </summary>
    /// <param name="url">The exact configured push URL.</param>
    /// <param name="expectedObjectId">The advertised destination OID, or no OID when absent.</param>
    /// <param name="relationship">The exact planned update relationship.</param>
    /// <param name="commitCount">The commits introduced by this source relative to the destination.</param>
    internal PushDestinationExpectation(
        RemoteUrl url,
        ObjectId? expectedObjectId,
        PushRelationship relationship,
        long commitCount)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentOutOfRangeException.ThrowIfNegative(commitCount);
        if (!Enum.IsDefined(relationship))
        {
            throw new ArgumentOutOfRangeException(nameof(relationship));
        }

        Url = url;
        ExpectedObjectId = expectedObjectId;
        Relationship = relationship;
        CommitCount = commitCount;
    }

    /// <summary>
    /// Gets the exact configured push URL for this destination.
    /// </summary>
    internal RemoteUrl Url { get; }

    /// <summary>
    /// Gets the exact advertised remote OID, or no OID when the destination is absent.
    /// </summary>
    internal ObjectId? ExpectedObjectId { get; }

    /// <summary>
    /// Gets the source relationship to this advertised destination.
    /// </summary>
    internal PushRelationship Relationship { get; }

    /// <summary>
    /// Gets the exact number of commits introduced relative to this destination.
    /// </summary>
    internal long CommitCount { get; }
}
