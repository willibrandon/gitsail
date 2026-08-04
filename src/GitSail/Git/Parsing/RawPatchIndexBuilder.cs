using GitSail.Domain;
using System.Collections.Immutable;

namespace GitSail.Git.Parsing;

/// <summary>
/// Builds a strict relative byte and presentation-line index while one exact file patch is streamed.
/// </summary>
internal sealed class RawPatchIndexBuilder
{
    private static ReadOnlySpan<byte> NoNewlineMarker => "\\ No newline at end of file"u8;
    private readonly long _fileOffset;
    private readonly ImmutableArray<RawPatchHunk>.Builder _hunks =
        ImmutableArray.CreateBuilder<RawPatchHunk>();
    private ImmutableArray<RawPatchLine>.Builder _lines =
        ImmutableArray.CreateBuilder<RawPatchLine>();
    private long _nextAbsoluteOffset;
    private int _lineNumber = 1;
    private int _headerLength = -1;
    private int _hunkOffset = -1;
    private int _hunkHeaderLength;
    private int _hunkStartLine;
    private int _oldStart;
    private int _oldCount;
    private int _newStart;
    private int _newCount;
    private int _observedOldCount;
    private int _observedNewCount;
    private bool _hasFirstLine;

    /// <summary>
    /// Initializes a streaming patch index at one absolute spool offset.
    /// </summary>
    /// <param name="fileOffset">The nonnegative absolute offset of the file patch.</param>
    internal RawPatchIndexBuilder(long fileOffset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fileOffset);
        _fileOffset = fileOffset;
        _nextAbsoluteOffset = fileOffset;
    }

    /// <summary>
    /// Adds one exact sequential patch line with semantic content separated from its retained terminator.
    /// </summary>
    /// <param name="content">The line bytes without a trailing carriage return or line feed.</param>
    /// <param name="absoluteOffset">The absolute spool offset of the line.</param>
    /// <param name="totalLength">The exact positive line length including any terminator.</param>
    internal void ProcessLine(
        ReadOnlySpan<byte> content,
        long absoluteOffset,
        int totalLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalLength);
        if (absoluteOffset != _nextAbsoluteOffset)
        {
            throw new InvalidDataException("Raw patch lines were not supplied in exact sequential order.");
        }

        var relativeOffset = ToRelativeOffset(absoluteOffset);
        _nextAbsoluteOffset = checked(absoluteOffset + totalLength);
        if (!_hasFirstLine)
        {
            if (relativeOffset != 0 || !content.StartsWith("diff --git "u8))
            {
                throw new InvalidDataException("A raw file patch did not begin with a Git diff header.");
            }

            _hasFirstLine = true;
            _lineNumber++;
            return;
        }

        if (content.StartsWith("@@ "u8))
        {
            CompleteHunk(relativeOffset, _lineNumber - 1);
            _headerLength = _headerLength < 0 ? relativeOffset : _headerLength;
            (_oldStart, _oldCount, _newStart, _newCount) = ParseHunkHeader(content);
            _hunkOffset = relativeOffset;
            _hunkHeaderLength = totalLength;
            _hunkStartLine = _lineNumber;
            _observedOldCount = 0;
            _observedNewCount = 0;
            _lines = ImmutableArray.CreateBuilder<RawPatchLine>();
        }
        else if (_hunkOffset >= 0)
        {
            if (content.IsEmpty)
            {
                throw new InvalidDataException("A raw patch hunk contained an unprefixed empty line.");
            }

            var kind = content[0] switch
            {
                (byte)' ' => RawPatchLineKind.Context,
                (byte)'+' => RawPatchLineKind.Addition,
                (byte)'-' => RawPatchLineKind.Deletion,
                (byte)'\\' when content.SequenceEqual(NoNewlineMarker) => RawPatchLineKind.NoNewlineMarker,
                _ => throw new InvalidDataException("A raw patch hunk contained an unknown line prefix."),
            };
            _observedOldCount += kind is RawPatchLineKind.Context or RawPatchLineKind.Deletion ? 1 : 0;
            _observedNewCount += kind is RawPatchLineKind.Context or RawPatchLineKind.Addition ? 1 : 0;
            _lines.Add(new RawPatchLine(relativeOffset, totalLength, _lineNumber, kind));
        }

        _lineNumber++;
    }

    /// <summary>
    /// Completes the streamed patch at its exact exclusive absolute end offset.
    /// </summary>
    /// <param name="absoluteEndOffset">The exclusive absolute end offset of the file patch.</param>
    /// <returns>The validated immutable relative patch index.</returns>
    internal RawPatchIndex Complete(long absoluteEndOffset)
    {
        if (!_hasFirstLine || absoluteEndOffset != _nextAbsoluteOffset)
        {
            throw new InvalidDataException("A raw file patch ended at an inconsistent byte offset.");
        }

        var fileLength = ToRelativeOffset(absoluteEndOffset);
        CompleteHunk(fileLength, _lineNumber - 1);
        return new RawPatchIndex(
            _headerLength < 0 ? fileLength : _headerLength,
            _hunks.ToImmutable());
    }

    private void CompleteHunk(int endOffset, int endLineNumber)
    {
        if (_hunkOffset < 0)
        {
            return;
        }

        if (_observedOldCount != _oldCount || _observedNewCount != _newCount)
        {
            throw new InvalidDataException("A raw patch hunk's content counts did not match its header.");
        }

        _hunks.Add(new RawPatchHunk(
            _hunkOffset,
            checked(endOffset - _hunkOffset),
            _hunkHeaderLength,
            _hunkStartLine,
            endLineNumber,
            _oldStart,
            _oldCount,
            _newStart,
            _newCount,
            _lines.ToImmutable()));
        _hunkOffset = -1;
    }

    private int ToRelativeOffset(long absoluteOffset)
    {
        var relativeOffset = checked(absoluteOffset - _fileOffset);
        if (relativeOffset is < 0 or > int.MaxValue)
        {
            throw new InvalidDataException("A raw file patch exceeded the supported indexed byte range.");
        }

        return (int)relativeOffset;
    }

    private static (int OldStart, int OldCount, int NewStart, int NewCount) ParseHunkHeader(
        ReadOnlySpan<byte> header)
    {
        var offset = 3;
        if (offset >= header.Length || header[offset] != (byte)'-')
        {
            throw new InvalidDataException("A raw patch hunk header omitted its old-side range.");
        }

        offset++;
        var oldStart = ParseDecimal(header, ref offset);
        var oldCount = ParseOptionalCount(header, ref offset);
        if (offset >= header.Length || header[offset] != (byte)' ')
        {
            throw new InvalidDataException("A raw patch hunk header did not separate its ranges.");
        }

        offset++;
        if (offset >= header.Length || header[offset] != (byte)'+')
        {
            throw new InvalidDataException("A raw patch hunk header omitted its new-side range.");
        }

        offset++;
        var newStart = ParseDecimal(header, ref offset);
        var newCount = ParseOptionalCount(header, ref offset);
        if (offset + 3 > header.Length || !header[offset..].StartsWith(" @@"u8))
        {
            throw new InvalidDataException("A raw patch hunk header omitted its closing marker.");
        }

        offset += 3;
        if (offset < header.Length && header[offset] != (byte)' ')
        {
            throw new InvalidDataException("A raw patch hunk header contained data beside its closing marker.");
        }

        return (oldStart, oldCount, newStart, newCount);
    }

    private static int ParseOptionalCount(ReadOnlySpan<byte> header, ref int offset)
    {
        if (offset >= header.Length || header[offset] != (byte)',')
        {
            return 1;
        }

        offset++;
        return ParseDecimal(header, ref offset);
    }

    private static int ParseDecimal(ReadOnlySpan<byte> header, ref int offset)
    {
        if (offset >= header.Length || header[offset] is < (byte)'0' or > (byte)'9')
        {
            throw new InvalidDataException("A raw patch hunk header contained an invalid decimal range.");
        }

        var value = 0;
        try
        {
            while (offset < header.Length && header[offset] is >= (byte)'0' and <= (byte)'9')
            {
                value = checked((value * 10) + header[offset] - (byte)'0');
                offset++;
            }
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("A raw patch hunk range exceeded the supported integer range.", exception);
        }

        return value;
    }
}
