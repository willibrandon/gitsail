using GitSail.Domain;
using System.Collections.Immutable;

namespace GitSail.Git.Parsing;

/// <summary>
/// Indexes exact byte ranges from Git diff3 output using one collision-checked marker set.
/// </summary>
internal static class ConflictMarkerParser
{
    /// <summary>
    /// Parses every complete three-way marker block while preserving the original merge bytes.
    /// </summary>
    /// <param name="content">The complete exact Git merge output.</param>
    /// <param name="markers">The exact unique markers supplied to that Git invocation.</param>
    /// <returns>An immutable exact merge document and its ordered conflict chunks.</returns>
    internal static ConflictMergeDocument Parse(
        ReadOnlySpan<byte> content,
        ConflictMarkerSet markers)
    {
        ArgumentNullException.ThrowIfNull(markers);
        var chunks = ImmutableArray.CreateBuilder<ConflictChunk>();
        var offset = 0;
        while (TryReadLine(content, offset, out var lineStart, out var lineEnd, out var nextOffset))
        {
            if (!content[lineStart..lineEnd].SequenceEqual(markers.OpeningMarker))
            {
                offset = nextOffset;
                continue;
            }

            var chunkStart = lineStart;
            var oursOffset = nextOffset;
            var baseMarker = FindLine(content, oursOffset, markers.BaseMarker, "base");
            var baseOffset = baseMarker.NextOffset;
            var separator = FindLine(content, baseOffset, markers.SeparatorMarker, "separator");
            var theirsOffset = separator.NextOffset;
            var closing = FindLine(content, theirsOffset, markers.ClosingMarker, "closing");
            chunks.Add(new ConflictChunk(
                chunks.Count,
                chunkStart,
                closing.NextOffset,
                oursOffset,
                baseMarker.StartOffset - oursOffset,
                baseOffset,
                separator.StartOffset - baseOffset,
                theirsOffset,
                closing.StartOffset - theirsOffset));
            offset = closing.NextOffset;
        }

        return new ConflictMergeDocument(content, chunks.ToImmutable());
    }

    private static (int StartOffset, int NextOffset) FindLine(
        ReadOnlySpan<byte> content,
        int offset,
        ReadOnlySpan<byte> marker,
        string markerName)
    {
        while (TryReadLine(content, offset, out var lineStart, out var lineEnd, out var nextOffset))
        {
            if (content[lineStart..lineEnd].SequenceEqual(marker))
            {
                return (lineStart, nextOffset);
            }

            offset = nextOffset;
        }

        throw new InvalidDataException($"Git merge output contained an incomplete {markerName} conflict marker.");
    }

    private static bool TryReadLine(
        ReadOnlySpan<byte> content,
        int offset,
        out int lineStart,
        out int lineEnd,
        out int nextOffset)
    {
        lineStart = offset;
        lineEnd = offset;
        nextOffset = offset;
        if ((uint)offset >= (uint)content.Length)
        {
            return false;
        }

        var relativeEnd = content[offset..].IndexOf((byte)'\n');
        if (relativeEnd < 0)
        {
            lineEnd = content.Length;
            nextOffset = content.Length;
        }
        else
        {
            lineEnd = offset + relativeEnd;
            nextOffset = lineEnd + 1;
        }

        if (lineEnd > lineStart && content[lineEnd - 1] == (byte)'\r')
        {
            lineEnd--;
        }

        return true;
    }
}
