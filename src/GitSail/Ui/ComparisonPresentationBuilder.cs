using GitSail.Domain;
using GitSail.Localization.Generated;
using System.Collections.Immutable;
using System.Globalization;

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
        var unifiedLineNumbers = new ComparisonLineNumber[CountPresentationLines(unified)];
        var left = new List<string>();
        var right = new List<string>();
        var sideHunkLines = ImmutableArray.CreateBuilder<int>();
        var sideLineNumbers = ImmutableArray.CreateBuilder<ComparisonLineNumber>();
        var unifiedHighlights = ImmutableArray.CreateBuilder<ComparisonHighlight>();
        var leftHighlights = ImmutableArray.CreateBuilder<ComparisonHighlight>();
        var rightHighlights = ImmutableArray.CreateBuilder<ComparisonHighlight>();
        if (file.IsBinary || file.PatchIndex.Hunks.IsEmpty)
        {
            var message = file.IsBinary
                ? AppMessages.DiffMessageBinaryComparison
                : AppMessages.DiffMessageMetadataOnly;
            left.Add(message);
            right.Add(message);
            sideLineNumbers.Add(default);
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
                sideLineNumbers.Add(default);
                AppendHunk(
                    patch,
                    hunk,
                    left,
                    right,
                    unifiedLineNumbers,
                    sideLineNumbers,
                    unifiedHighlights,
                    leftHighlights,
                    rightHighlights);
            }
        }

        if (isTruncated)
        {
            var marker = AppMessages.DiffMessagePresentationTruncated;
            AddAligned(left, right, marker, marker);
            sideLineNumbers.Add(default);
        }

        return new ComparisonPresentation(
            unified,
            JoinLines(left),
            JoinLines(right),
            unifiedHunkLines.ToImmutable(),
            sideHunkLines.ToImmutable(),
            unifiedHighlights.ToImmutable(),
            leftHighlights.ToImmutable(),
            rightHighlights.ToImmutable(),
            [.. unifiedLineNumbers],
            sideLineNumbers.ToImmutable());
    }

    private static void AppendHunk(
        ReadOnlySpan<byte> patch,
        RawPatchHunk hunk,
        List<string> left,
        List<string> right,
        ComparisonLineNumber[] unifiedLineNumbers,
        ImmutableArray<ComparisonLineNumber>.Builder sideLineNumbers,
        ImmutableArray<ComparisonHighlight>.Builder unifiedHighlights,
        ImmutableArray<ComparisonHighlight>.Builder leftHighlights,
        ImmutableArray<ComparisonHighlight>.Builder rightHighlights)
    {
        var oldLine = hunk.OldStart;
        var newLine = hunk.NewStart;
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
                var lineNumber = new ComparisonLineNumber(oldLine, newLine);
                SetUnifiedLineNumber(unifiedLineNumbers, line.LineNumber, lineNumber);
                sideLineNumbers.Add(lineNumber);
                oldLine++;
                newLine++;
                lineIndex++;
                continue;
            }

            if (line.Kind == RawPatchLineKind.NoNewlineMarker)
            {
                var marker = DecodeLine(patch.Slice(line.Offset, line.Length));
                AddAligned(left, right, marker, marker);
                sideLineNumbers.Add(default);
                lineIndex++;
                continue;
            }

            var deletions = new List<(string Text, int UnifiedLine, int FileLine)>();
            var additions = new List<(string Text, int UnifiedLine, int FileLine)>();
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
                    deletions.Add((content, line.LineNumber, oldLine));
                    SetUnifiedLineNumber(
                        unifiedLineNumbers,
                        line.LineNumber,
                        new ComparisonLineNumber(oldLine, null));
                    oldLine++;
                }
                else
                {
                    additions.Add((content, line.LineNumber, newLine));
                    SetUnifiedLineNumber(
                        unifiedLineNumbers,
                        line.LineNumber,
                        new ComparisonLineNumber(null, newLine));
                    newLine++;
                }

                lineIndex++;
            }

            var rowCount = Math.Max(deletions.Count, additions.Count);
            for (var row = 0; row < rowCount; row++)
            {
                var sideLine = left.Count + 1;
                if (row < deletions.Count && row < additions.Count)
                {
                    AddIntralineHighlights(
                        deletions[row],
                        additions[row],
                        sideLine,
                        unifiedHighlights,
                        leftHighlights,
                        rightHighlights);
                }

                AddAligned(
                    left,
                    right,
                    row < deletions.Count ? "-" + deletions[row].Text : string.Empty,
                    row < additions.Count ? "+" + additions[row].Text : string.Empty);
                sideLineNumbers.Add(new ComparisonLineNumber(
                    row < deletions.Count ? deletions[row].FileLine : null,
                    row < additions.Count ? additions[row].FileLine : null));
            }
        }
    }

    private static void AddIntralineHighlights(
        (string Text, int UnifiedLine, int FileLine) deletion,
        (string Text, int UnifiedLine, int FileLine) addition,
        int sideLine,
        ImmutableArray<ComparisonHighlight>.Builder unifiedHighlights,
        ImmutableArray<ComparisonHighlight>.Builder leftHighlights,
        ImmutableArray<ComparisonHighlight>.Builder rightHighlights)
    {
        var deletionBoundaries = StringInfo.ParseCombiningCharacters(deletion.Text);
        var additionBoundaries = StringInfo.ParseCombiningCharacters(addition.Text);
        var commonPrefix = CountCommonPrefix(
            deletion.Text,
            deletionBoundaries,
            addition.Text,
            additionBoundaries);
        var commonSuffix = CountCommonSuffix(
            deletion.Text,
            deletionBoundaries,
            addition.Text,
            additionBoundaries,
            commonPrefix);
        var deletionStart = GetElementOffset(deletionBoundaries, commonPrefix, deletion.Text.Length);
        var additionStart = GetElementOffset(additionBoundaries, commonPrefix, addition.Text.Length);
        var deletionEnd = GetElementOffset(
            deletionBoundaries,
            deletionBoundaries.Length - commonSuffix,
            deletion.Text.Length);
        var additionEnd = GetElementOffset(
            additionBoundaries,
            additionBoundaries.Length - commonSuffix,
            addition.Text.Length);

        AddHighlight(
            deletion.UnifiedLine,
            deletionStart,
            deletionEnd,
            isAddition: false,
            unifiedHighlights);
        AddHighlight(
            addition.UnifiedLine,
            additionStart,
            additionEnd,
            isAddition: true,
            unifiedHighlights);
        AddHighlight(
            sideLine,
            deletionStart,
            deletionEnd,
            isAddition: false,
            leftHighlights);
        AddHighlight(
            sideLine,
            additionStart,
            additionEnd,
            isAddition: true,
            rightHighlights);
    }

    private static int CountCommonPrefix(
        string deletion,
        int[] deletionBoundaries,
        string addition,
        int[] additionBoundaries)
    {
        var maximum = Math.Min(deletionBoundaries.Length, additionBoundaries.Length);
        var count = 0;
        while (count < maximum && ElementsEqual(
            deletion,
            deletionBoundaries,
            count,
            addition,
            additionBoundaries,
            count))
        {
            count++;
        }

        return count;
    }

    private static int CountCommonSuffix(
        string deletion,
        int[] deletionBoundaries,
        string addition,
        int[] additionBoundaries,
        int commonPrefix)
    {
        var maximum = Math.Min(
            deletionBoundaries.Length - commonPrefix,
            additionBoundaries.Length - commonPrefix);
        var count = 0;
        while (count < maximum && ElementsEqual(
            deletion,
            deletionBoundaries,
            deletionBoundaries.Length - count - 1,
            addition,
            additionBoundaries,
            additionBoundaries.Length - count - 1))
        {
            count++;
        }

        return count;
    }

    private static bool ElementsEqual(
        string left,
        int[] leftBoundaries,
        int leftIndex,
        string right,
        int[] rightBoundaries,
        int rightIndex)
    {
        var leftStart = leftBoundaries[leftIndex];
        var rightStart = rightBoundaries[rightIndex];
        var leftLength = GetElementOffset(leftBoundaries, leftIndex + 1, left.Length) - leftStart;
        var rightLength = GetElementOffset(rightBoundaries, rightIndex + 1, right.Length) - rightStart;
        return leftLength == rightLength &&
            left.AsSpan(leftStart, leftLength).SequenceEqual(right.AsSpan(rightStart, rightLength));
    }

    private static int GetElementOffset(int[] boundaries, int index, int textLength)
        => index >= boundaries.Length ? textLength : boundaries[index];

    private static void AddHighlight(
        int line,
        int contentStart,
        int contentEnd,
        bool isAddition,
        ImmutableArray<ComparisonHighlight>.Builder highlights)
    {
        if (contentStart >= contentEnd)
        {
            return;
        }

        const int patchPrefixColumn = 2;
        highlights.Add(new ComparisonHighlight(
            line,
            patchPrefixColumn + contentStart,
            patchPrefixColumn + contentEnd,
            isAddition));
    }

    private static void SetUnifiedLineNumber(
        ComparisonLineNumber[] lineNumbers,
        int presentationLine,
        ComparisonLineNumber lineNumber)
    {
        if (presentationLine > 0 && presentationLine <= lineNumbers.Length)
        {
            lineNumbers[presentationLine - 1] = lineNumber;
        }
    }

    private static int CountPresentationLines(string text)
    {
        var count = 1;
        foreach (var character in text)
        {
            if (character == '\n')
            {
                count++;
            }
        }

        return count;
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
