using System.Collections.Immutable;

namespace GitSail.Domain;

/// <summary>
/// Owns exact three-way merge bytes and validated conflict-chunk offsets for resolution planning.
/// </summary>
internal sealed class ConflictMergeDocument
{
    private readonly byte[] _content;

    /// <summary>
    /// Initializes one immutable merge document from independently owned exact bytes and chunks.
    /// </summary>
    /// <param name="content">The complete merge output including collision-checked markers.</param>
    /// <param name="chunks">The validated chunks in non-overlapping document order.</param>
    internal ConflictMergeDocument(
        ReadOnlySpan<byte> content,
        ImmutableArray<ConflictChunk> chunks)
    {
        if (chunks.IsDefault)
        {
            throw new ArgumentException("Conflict chunks must be initialized.", nameof(chunks));
        }

        var previousEnd = 0;
        for (var index = 0; index < chunks.Length; index++)
        {
            var chunk = chunks[index];
            if (chunk.Index != index || chunk.StartOffset < previousEnd || chunk.EndOffset > content.Length)
            {
                throw new ArgumentException("Conflict chunks are not ordered within the merge document.", nameof(chunks));
            }

            previousEnd = chunk.EndOffset;
        }

        _content = content.ToArray();
        Chunks = chunks;
    }

    /// <summary>
    /// Gets the complete exact merge output including unresolved marker blocks.
    /// </summary>
    internal ReadOnlyMemory<byte> Content => _content;

    /// <summary>
    /// Gets every indexed conflict chunk in document order.
    /// </summary>
    internal ImmutableArray<ConflictChunk> Chunks { get; }

    /// <summary>
    /// Builds exact marker-free bytes by applying one explicit choice to every conflict chunk.
    /// </summary>
    /// <param name="choices">One resolution choice for each chunk in document order.</param>
    /// <returns>The exact resolved content with all non-conflicting bytes preserved.</returns>
    internal byte[] BuildResolvedContent(IReadOnlyList<ConflictResolutionChoice> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        if (choices.Count != Chunks.Length)
        {
            throw new ArgumentException("Every conflict chunk requires exactly one resolution choice.", nameof(choices));
        }

        using var output = new MemoryStream(_content.Length);
        var sourceOffset = 0;
        for (var index = 0; index < Chunks.Length; index++)
        {
            var chunk = Chunks[index];
            output.Write(_content, sourceOffset, chunk.StartOffset - sourceOffset);
            WriteChoice(output, chunk, choices[index]);
            sourceOffset = chunk.EndOffset;
        }

        output.Write(_content, sourceOffset, _content.Length - sourceOffset);
        return output.ToArray();
    }

    private void WriteChoice(
        MemoryStream output,
        ConflictChunk chunk,
        ConflictResolutionChoice choice)
    {
        switch (choice)
        {
            case ConflictResolutionChoice.Ours:
                output.Write(_content, chunk.OursOffset, chunk.OursLength);
                break;
            case ConflictResolutionChoice.Theirs:
                output.Write(_content, chunk.TheirsOffset, chunk.TheirsLength);
                break;
            case ConflictResolutionChoice.Base:
                output.Write(_content, chunk.BaseOffset, chunk.BaseLength);
                break;
            case ConflictResolutionChoice.Both:
                output.Write(_content, chunk.OursOffset, chunk.OursLength);
                output.Write(_content, chunk.TheirsOffset, chunk.TheirsLength);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(choice));
        }
    }
}
