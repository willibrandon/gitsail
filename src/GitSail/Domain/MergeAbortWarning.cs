using System.Collections.Immutable;

namespace GitSail.Domain;

/// <summary>
/// Describes the exact in-progress merge state for which abort confirmation is required.
/// </summary>
internal sealed class MergeAbortWarning
{
    private const int Sha256Bytes = 32;
    private readonly byte[] _workTreeFingerprint;

    /// <summary>
    /// Initializes one immutable warning from the displayed repository and merge-head state.
    /// </summary>
    /// <param name="precondition">The exact displayed HEAD, symbolic attachment, and index contents.</param>
    /// <param name="mergeHeads">The exact nonempty MERGE_HEAD object sequence.</param>
    /// <param name="workTreeFingerprint">The SHA-256 fingerprint of Git's complete binary worktree diff.</param>
    /// <param name="mergeAutostash">The optional exact MERGE_AUTOSTASH object Git will apply.</param>
    internal MergeAbortWarning(
        RepositoryPrecondition precondition,
        IEnumerable<ObjectId> mergeHeads,
        ReadOnlySpan<byte> workTreeFingerprint,
        ObjectId? mergeAutostash = null)
    {
        ArgumentNullException.ThrowIfNull(precondition);
        ArgumentNullException.ThrowIfNull(mergeHeads);
        if (precondition.HeadObjectId is null)
        {
            throw new ArgumentException("An in-progress merge must have an existing HEAD.", nameof(precondition));
        }

        if (workTreeFingerprint.Length != Sha256Bytes)
        {
            throw new ArgumentException(
                "A worktree fingerprint must contain exactly 32 bytes.",
                nameof(workTreeFingerprint));
        }

        var builder = ImmutableArray.CreateBuilder<ObjectId>();
        foreach (var mergeHead in mergeHeads)
        {
            ArgumentNullException.ThrowIfNull(mergeHead);
            if (mergeHead.Format != precondition.HeadObjectId.Format)
            {
                throw new ArgumentException(
                    "Every MERGE_HEAD must use the repository HEAD object format.",
                    nameof(mergeHeads));
            }

            builder.Add(mergeHead);
        }

        if (builder.Count == 0)
        {
            throw new ArgumentException("An in-progress merge must contain at least one MERGE_HEAD.", nameof(mergeHeads));
        }

        if (mergeAutostash is not null && mergeAutostash.Format != precondition.HeadObjectId.Format)
        {
            throw new ArgumentException(
                "MERGE_AUTOSTASH must use the repository HEAD object format.",
                nameof(mergeAutostash));
        }

        Precondition = precondition;
        MergeHeads = builder.ToImmutable();
        _workTreeFingerprint = workTreeFingerprint.ToArray();
        MergeAutostash = mergeAutostash;
    }

    /// <summary>
    /// Gets the exact displayed HEAD, symbolic attachment, and index contents.
    /// </summary>
    internal RepositoryPrecondition Precondition { get; }

    /// <summary>
    /// Gets the exact ordered MERGE_HEAD objects shown to the user.
    /// </summary>
    internal ImmutableArray<ObjectId> MergeHeads { get; }

    /// <summary>
    /// Gets the immutable SHA-256 fingerprint of Git's complete binary worktree diff.
    /// </summary>
    internal ReadOnlyMemory<byte> WorkTreeFingerprint => _workTreeFingerprint;

    /// <summary>
    /// Gets the optional exact autostash object Git will apply while aborting the merge.
    /// </summary>
    internal ObjectId? MergeAutostash { get; }

    /// <summary>
    /// Determines whether another warning identifies the same complete merge transaction state.
    /// </summary>
    /// <param name="other">The independently captured warning to compare.</param>
    /// <returns><see langword="true"/> only when every guarded repository and merge value matches.</returns>
    internal bool Matches(MergeAbortWarning? other)
        => other is not null &&
            Precondition.Matches(other.Precondition) &&
            MergeHeads.AsSpan().SequenceEqual(other.MergeHeads.AsSpan()) &&
            _workTreeFingerprint.AsSpan().SequenceEqual(other._workTreeFingerprint) &&
            Equals(MergeAutostash, other.MergeAutostash);
}
