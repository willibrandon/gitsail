using GitSail.Domain;
using System.Buffers;
using System.Globalization;
using System.Text;

namespace GitSail.Git.Parsing;

/// <summary>
/// Builds an applicable exact-byte file patch from an original header and selected complete hunks.
/// </summary>
internal static class RawPatchSelectionBuilder
{
    private static ReadOnlySpan<byte> ClosingHunkMarker => " @@"u8;

    /// <summary>
    /// Copies one complete original hunk behind the unchanged original file header.
    /// </summary>
    /// <param name="patch">The complete exact original file patch.</param>
    /// <param name="index">The validated byte index for the patch.</param>
    /// <param name="hunk">The selected hunk owned by the index.</param>
    /// <returns>A new applicable patch containing only the selected hunk.</returns>
    internal static byte[] BuildSingleHunk(
        ReadOnlySpan<byte> patch,
        RawPatchIndex index,
        RawPatchHunk hunk)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(hunk);
        if (!index.Hunks.Contains(hunk))
        {
            throw new ArgumentException("The selected hunk does not belong to this patch index.", nameof(hunk));
        }

        if (index.HeaderLength < 0 || index.HeaderLength > patch.Length ||
            hunk.Offset < index.HeaderLength || hunk.Length > patch.Length - hunk.Offset)
        {
            throw new InvalidDataException("The raw patch index contained an out-of-range slice.");
        }

        var result = new byte[checked(index.HeaderLength + hunk.Length)];
        patch[..index.HeaderLength].CopyTo(result);
        patch.Slice(hunk.Offset, hunk.Length).CopyTo(result.AsSpan(index.HeaderLength));
        return result;
    }

    /// <summary>
    /// Builds one exact patch containing every selected changed line across one or more hunks.
    /// </summary>
    /// <param name="patch">The complete exact original file patch.</param>
    /// <param name="index">The validated byte index for the patch.</param>
    /// <param name="selectedLineNumbers">The discontiguous presentation lines selected for mutation.</param>
    /// <param name="selectionSide">The unchanged side retained for the intended apply direction.</param>
    /// <returns>A new applicable patch containing only the selected line changes.</returns>
    internal static byte[] BuildSelectedLines(
        ReadOnlySpan<byte> patch,
        RawPatchIndex index,
        IReadOnlySet<int> selectedLineNumbers,
        RawPatchSelectionSide selectionSide)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(selectedLineNumbers);
        ValidateSelectionSide(selectionSide);
        if (selectedLineNumbers.Count == 0)
        {
            throw new ArgumentException("A line patch requires at least one selected line.", nameof(selectedLineNumbers));
        }

        if (index.HeaderLength < 0 || index.HeaderLength > patch.Length)
        {
            throw new InvalidDataException("The raw patch index contained an out-of-range header slice.");
        }

        var writer = new ArrayBufferWriter<byte>(Math.Min(patch.Length, 64 * 1024));
        writer.Write(patch[..index.HeaderLength]);
        var selectedHunkCount = 0;
        foreach (var hunk in index.Hunks)
        {
            if (hunk.Offset < index.HeaderLength || hunk.Length > patch.Length - hunk.Offset)
            {
                throw new InvalidDataException("The raw patch index contained an out-of-range hunk slice.");
            }

            var selectedHunk = BuildSelectedHunk(
                patch.Slice(hunk.Offset, hunk.Length),
                hunk,
                selectedLineNumbers,
                selectionSide);
            if (selectedHunk.Length == 0)
            {
                continue;
            }

            writer.Write(selectedHunk);
            selectedHunkCount++;
        }

        if (selectedHunkCount == 0)
        {
            throw new ArgumentException(
                "The selected presentation lines did not contain a changed patch line.",
                nameof(selectedLineNumbers));
        }

        return writer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Builds one regenerated hunk body from an exact original hunk and selected changed lines.
    /// </summary>
    /// <param name="hunkPatch">The exact original bytes beginning with this hunk's header.</param>
    /// <param name="hunk">The validated hunk index whose offsets refer to its complete file patch.</param>
    /// <param name="selectedLineNumbers">The discontiguous presentation lines selected for mutation.</param>
    /// <param name="selectionSide">The unchanged side retained for the intended apply direction.</param>
    /// <returns>The regenerated hunk bytes, or an empty array when this hunk has no selected change.</returns>
    internal static byte[] BuildSelectedHunk(
        ReadOnlySpan<byte> hunkPatch,
        RawPatchHunk hunk,
        IReadOnlySet<int> selectedLineNumbers,
        RawPatchSelectionSide selectionSide)
    {
        ArgumentNullException.ThrowIfNull(hunk);
        ArgumentNullException.ThrowIfNull(selectedLineNumbers);
        ValidateSelectionSide(selectionSide);
        if (!hunk.Lines.Any(line =>
            selectedLineNumbers.Contains(line.LineNumber) &&
            line.Kind is RawPatchLineKind.Addition or RawPatchLineKind.Deletion))
        {
            return [];
        }

        if (hunk.HeaderLength <= 0 || hunk.HeaderLength > hunkPatch.Length)
        {
            throw new InvalidDataException("The raw patch hunk contained an out-of-range header slice.");
        }

        var oldCount = 0;
        var newCount = 0;
        foreach (var line in hunk.Lines)
        {
            var outputKind = GetOutputKind(line, selectedLineNumbers, selectionSide);
            oldCount += outputKind is RawPatchLineKind.Context or RawPatchLineKind.Deletion ? 1 : 0;
            newCount += outputKind is RawPatchLineKind.Context or RawPatchLineKind.Addition ? 1 : 0;
        }

        var writer = new ArrayBufferWriter<byte>(Math.Min(hunkPatch.Length, 64 * 1024));
        WriteHunkHeader(writer, hunkPatch[..hunk.HeaderLength], hunk, oldCount, newCount);
        var previousContentIncluded = false;
        foreach (var line in hunk.Lines)
        {
            var relativeOffset = checked(line.Offset - hunk.Offset);
            if (relativeOffset < hunk.HeaderLength || line.Length > hunkPatch.Length - relativeOffset)
            {
                throw new InvalidDataException("The raw patch hunk contained an out-of-range line slice.");
            }

            var lineBytes = hunkPatch.Slice(relativeOffset, line.Length);
            if (line.Kind == RawPatchLineKind.NoNewlineMarker)
            {
                if (previousContentIncluded)
                {
                    writer.Write(lineBytes);
                }

                continue;
            }

            var outputKind = GetOutputKind(line, selectedLineNumbers, selectionSide);
            previousContentIncluded = outputKind is not null;
            if (outputKind is null)
            {
                continue;
            }

            if (outputKind == line.Kind)
            {
                writer.Write(lineBytes);
                continue;
            }

            var prefix = writer.GetSpan(1);
            prefix[0] = (byte)' ';
            writer.Advance(1);
            writer.Write(lineBytes[1..]);
        }

        return writer.WrittenSpan.ToArray();
    }

    private static RawPatchLineKind? GetOutputKind(
        RawPatchLine line,
        IReadOnlySet<int> selectedLineNumbers,
        RawPatchSelectionSide selectionSide)
    {
        if (line.Kind is RawPatchLineKind.Context or RawPatchLineKind.NoNewlineMarker ||
            selectedLineNumbers.Contains(line.LineNumber))
        {
            return line.Kind;
        }

        return selectionSide switch
        {
            RawPatchSelectionSide.PreserveOldSide => line.Kind switch
            {
                RawPatchLineKind.Deletion => RawPatchLineKind.Context,
                RawPatchLineKind.Addition => null,
                _ => line.Kind,
            },
            RawPatchSelectionSide.PreserveNewSide => line.Kind switch
            {
                RawPatchLineKind.Addition => RawPatchLineKind.Context,
                RawPatchLineKind.Deletion => null,
                _ => line.Kind,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(selectionSide)),
        };
    }

    private static void WriteHunkHeader(
        ArrayBufferWriter<byte> writer,
        ReadOnlySpan<byte> originalHeader,
        RawPatchHunk hunk,
        int oldCount,
        int newCount)
    {
        var suffixOffset = FindHeaderSuffixOffset(originalHeader);
        var generatedHeader = FormattableString.Invariant(
            $"@@ -{FormatRange(hunk.OldStart, oldCount)} +{FormatRange(hunk.NewStart, newCount)} @@");
        writer.Write(Encoding.ASCII.GetBytes(generatedHeader));
        writer.Write(originalHeader[suffixOffset..]);
    }

    private static int FindHeaderSuffixOffset(ReadOnlySpan<byte> originalHeader)
    {
        var markerOffset = originalHeader[3..].IndexOf(ClosingHunkMarker);
        if (markerOffset < 0)
        {
            throw new InvalidDataException("The raw patch hunk header omitted its closing marker.");
        }

        return checked(3 + markerOffset + ClosingHunkMarker.Length);
    }

    private static string FormatRange(int start, int count)
        => count == 1
            ? start.ToString(CultureInfo.InvariantCulture)
            : FormattableString.Invariant($"{start},{count}");

    private static void ValidateSelectionSide(RawPatchSelectionSide selectionSide)
    {
        if (selectionSide is not RawPatchSelectionSide.PreserveOldSide and
            not RawPatchSelectionSide.PreserveNewSide)
        {
            throw new ArgumentOutOfRangeException(nameof(selectionSide));
        }
    }
}
