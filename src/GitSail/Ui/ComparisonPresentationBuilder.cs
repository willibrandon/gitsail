using GitSail.Domain;
using System.Collections.Immutable;

namespace GitSail.Ui;

/// <summary>
/// Builds bounded unified and line-aligned two-pane text from exact indexed patch bytes.
/// </summary>
internal static class ComparisonPresentationBuilder
{
    /// <summary>
    /// Builds both comparison layouts without using presentation text as repository input.
    /// </summary>
    /// <param name="patch">The bounded exact prefix of one raw file patch.</param>
    /// <param name="file">The byte index describing the complete raw file patch.</param>
    /// <param name="isTruncated">Whether exact bytes remain outside the supplied prefix.</param>
    /// <returns>The unified and aligned two-pane presentation documents.</returns>
    internal static ComparisonPresentation Build(
        ReadOnlySpan<byte> patch,
        RawDiffFile file,
        bool isTruncated)
    {
        ArgumentNullException.ThrowIfNull(file);
        var unifiedHunkLines = ImmutableArray.CreateBuilder<int>();
        foreach (var hunk in file.PatchIndex.Hunks)
        {
            if (IsAvailable(patch, hunk.Offset, hunk.HeaderLength))
            {
                unifiedHunkLines.Add(hunk.StartLineNumber);
            }
        }
        var unified = RawPatchPresentationDecoder.Decode(patch, isTruncated);
        var left = new List<string>();
        var right = new List<string>();
        var sideHunkLines = ImmutableArray.CreateBuilder<int>();
        if (file.IsBinary || file.PatchIndex.Hunks.IsEmpty)
        {
            var message = file.IsBinary
                ? "Binary comparison. Unified view contains Git's exact binary metadata."
                : "This comparison changes file metadata without textual hunks.";
            left.Add(message);
            right.Add(message);
        }
        else
        {
            foreach (var hunk in file.PatchIndex.Hunks)
            {
                if (!IsAvailable(patch, hunk.Offset, hunk.HeaderLength))
                {
                    break;
                }

                sideHunkLines.Add(left.Count + 1);
                var header = DecodeLine(patch.Slice(hunk.Offset, hunk.HeaderLength));
                AddAligned(left, right, header, header);
                AppendHunk(patch, hunk, left, right);
            }
        }

        if (isTruncated)
        {
            const string marker = "<presentation truncated; exact patch bytes remain in the comparison spool>";
            AddAligned(left, right, marker, marker);
        }

        return new ComparisonPresentation(
            unified,
            JoinLines(left),
            JoinLines(right),
            unifiedHunkLines.ToImmutable(),
            sideHunkLines.ToImmutable());
    }

    private static void AppendHunk(
        ReadOnlySpan<byte> patch,
        RawPatchHunk hunk,
        List<string> left,
        List<string> right)
    {
        var lineIndex = 0;
        while (lineIndex < hunk.Lines.Length)
        {
            var line = hunk.Lines[lineIndex];
            if (!IsAvailable(patch, line.Offset, line.Length))
            {
                return;
            }

            if (line.Kind == RawPatchLineKind.Context)
            {
                var content = DecodePatchContent(patch, line);
                AddAligned(left, right, " " + content, " " + content);
                lineIndex++;
                continue;
            }

            if (line.Kind == RawPatchLineKind.NoNewlineMarker)
            {
                var marker = DecodeLine(patch.Slice(line.Offset, line.Length));
                AddAligned(left, right, marker, marker);
                lineIndex++;
                continue;
            }

            var deletions = new List<string>();
            var additions = new List<string>();
            while (lineIndex < hunk.Lines.Length &&
                hunk.Lines[lineIndex].Kind is RawPatchLineKind.Deletion or RawPatchLineKind.Addition)
            {
                line = hunk.Lines[lineIndex];
                if (!IsAvailable(patch, line.Offset, line.Length))
                {
                    return;
                }

                var content = DecodePatchContent(patch, line);
                if (line.Kind == RawPatchLineKind.Deletion)
                {
                    deletions.Add("-" + content);
                }
                else
                {
                    additions.Add("+" + content);
                }

                lineIndex++;
            }

            var rowCount = Math.Max(deletions.Count, additions.Count);
            for (var row = 0; row < rowCount; row++)
            {
                AddAligned(
                    left,
                    right,
                    row < deletions.Count ? deletions[row] : string.Empty,
                    row < additions.Count ? additions[row] : string.Empty);
            }
        }
    }

    private static string DecodePatchContent(ReadOnlySpan<byte> patch, RawPatchLine line)
    {
        var bytes = patch.Slice(line.Offset, line.Length);
        return bytes.IsEmpty ? string.Empty : DecodeLine(bytes[1..]);
    }

    private static string DecodeLine(ReadOnlySpan<byte> bytes)
    {
        var text = RawPatchPresentationDecoder.Decode(bytes, isTruncated: false);
        return text.EndsWith('\n') ? text[..^1] : text;
    }

    private static bool IsAvailable(ReadOnlySpan<byte> patch, int offset, int length)
        => offset >= 0 && length >= 0 && offset <= patch.Length && length <= patch.Length - offset;

    private static void AddAligned(
        List<string> left,
        List<string> right,
        string leftLine,
        string rightLine)
    {
        left.Add(leftLine);
        right.Add(rightLine);
    }

    private static string JoinLines(List<string> lines)
        => lines.Count == 0 ? string.Empty : string.Join('\n', lines) + "\n";
}
