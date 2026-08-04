namespace GitSail.Domain;

/// <summary>
/// Indexes exact marker and side-content byte ranges for one parsed three-way conflict chunk.
/// </summary>
internal sealed class ConflictChunk
{
    /// <summary>
    /// Initializes one validated conflict chunk over a shared immutable merge document.
    /// </summary>
    /// <param name="index">The zero-based chunk index in document order.</param>
    /// <param name="startOffset">The inclusive opening-marker offset.</param>
    /// <param name="endOffset">The exclusive closing-marker-line offset.</param>
    /// <param name="oursOffset">The inclusive current-side content offset.</param>
    /// <param name="oursLength">The exact current-side content length.</param>
    /// <param name="baseOffset">The inclusive merge-base content offset.</param>
    /// <param name="baseLength">The exact merge-base content length.</param>
    /// <param name="theirsOffset">The inclusive incoming-side content offset.</param>
    /// <param name="theirsLength">The exact incoming-side content length.</param>
    internal ConflictChunk(
        int index,
        int startOffset,
        int endOffset,
        int oursOffset,
        int oursLength,
        int baseOffset,
        int baseLength,
        int theirsOffset,
        int theirsLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfNegative(startOffset);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(endOffset, startOffset);
        ValidateRange(oursOffset, oursLength, startOffset, endOffset, nameof(oursOffset));
        ValidateRange(baseOffset, baseLength, oursOffset + oursLength, endOffset, nameof(baseOffset));
        ValidateRange(theirsOffset, theirsLength, baseOffset + baseLength, endOffset, nameof(theirsOffset));
        Index = index;
        StartOffset = startOffset;
        EndOffset = endOffset;
        OursOffset = oursOffset;
        OursLength = oursLength;
        BaseOffset = baseOffset;
        BaseLength = baseLength;
        TheirsOffset = theirsOffset;
        TheirsLength = theirsLength;
    }

    /// <summary>
    /// Gets the zero-based conflict index in document order.
    /// </summary>
    internal int Index { get; }

    /// <summary>
    /// Gets the inclusive byte offset of the opening marker line.
    /// </summary>
    internal int StartOffset { get; }

    /// <summary>
    /// Gets the exclusive byte offset after the closing marker line.
    /// </summary>
    internal int EndOffset { get; }

    /// <summary>
    /// Gets the inclusive byte offset of exact current-side content.
    /// </summary>
    internal int OursOffset { get; }

    /// <summary>
    /// Gets the exact current-side content byte length.
    /// </summary>
    internal int OursLength { get; }

    /// <summary>
    /// Gets the inclusive byte offset of exact merge-base content.
    /// </summary>
    internal int BaseOffset { get; }

    /// <summary>
    /// Gets the exact merge-base content byte length.
    /// </summary>
    internal int BaseLength { get; }

    /// <summary>
    /// Gets the inclusive byte offset of exact incoming-side content.
    /// </summary>
    internal int TheirsOffset { get; }

    /// <summary>
    /// Gets the exact incoming-side content byte length.
    /// </summary>
    internal int TheirsLength { get; }

    private static void ValidateRange(
        int offset,
        int length,
        int minimumOffset,
        int maximumOffset,
        string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset, parameterName);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (offset < minimumOffset || offset > maximumOffset || length > maximumOffset - offset)
        {
            throw new ArgumentOutOfRangeException(parameterName, "A conflict content range is outside its marker block.");
        }
    }
}
