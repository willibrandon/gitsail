using GitSail.Domain;
using System.Collections.Immutable;
using System.Text;

namespace GitSail.Git.Parsing;

/// <summary>
/// Parses bounded NUL-delimited Git porcelain version 2 status output without decoding Unix paths.
/// </summary>
internal sealed class PorcelainV2StatusParser
{
    private const int DefaultMaximumRecordBytes = 16 * 1024 * 1024;
    private readonly int _maximumRecordBytes;

    /// <summary>
    /// Initializes a status parser with a bounded maximum record size.
    /// </summary>
    /// <param name="maximumRecordBytes">The positive maximum byte count for one NUL-delimited record.</param>
    internal PorcelainV2StatusParser(int maximumRecordBytes = DefaultMaximumRecordBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRecordBytes);
        _maximumRecordBytes = maximumRecordBytes;
    }

    /// <summary>
    /// Parses a complete bounded status response into an immutable generation snapshot.
    /// </summary>
    /// <param name="bytes">The complete NUL-delimited status response.</param>
    /// <param name="repository">The repository that produced the response.</param>
    /// <param name="generation">The operation generation assigned to the response.</param>
    /// <returns>The immutable structured status snapshot.</returns>
    internal RepositoryStatusSnapshot Parse(
        ReadOnlySpan<byte> bytes,
        RepositoryLocation repository,
        OperationGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(repository);

        ObjectId? headObjectId = null;
        RefName? headName = null;
        RefName? upstreamName = null;
        var aheadCount = 0;
        var behindCount = 0;
        var entries = ImmutableArray.CreateBuilder<RepositoryStatusEntry>();
        while (!bytes.IsEmpty)
        {
            var record = TakeRecord(ref bytes);
            if (record.IsEmpty)
            {
                throw new InvalidDataException("Git status contained an empty record.");
            }

            switch (record[0])
            {
                case (byte)'#':
                    ParseHeader(record, ref headObjectId, ref headName, ref upstreamName, ref aheadCount, ref behindCount);
                    break;
                case (byte)'1':
                    entries.Add(ParseOrdinary(record));
                    break;
                case (byte)'2':
                    entries.Add(ParseRenameOrCopy(record, TakeRecord(ref bytes)));
                    break;
                case (byte)'u':
                    entries.Add(ParseUnmerged(record));
                    break;
                case (byte)'?':
                    entries.Add(ParseSimple(record, RepositoryStatusEntryKind.Untracked, GitFileStatus.Untracked));
                    break;
                case (byte)'!':
                    entries.Add(ParseSimple(record, RepositoryStatusEntryKind.Ignored, GitFileStatus.Ignored));
                    break;
                default:
                    throw new InvalidDataException("Git status contained an unknown record type.");
            }
        }

        return new RepositoryStatusSnapshot(
            generation,
            repository,
            headObjectId,
            headName,
            upstreamName,
            aheadCount,
            behindCount,
            entries.ToImmutable());
    }

    private ReadOnlySpan<byte> TakeRecord(ref ReadOnlySpan<byte> bytes)
    {
        var terminator = bytes.IndexOf((byte)0);
        if (terminator < 0)
        {
            throw new InvalidDataException("Git status ended before a NUL record terminator.");
        }

        if (terminator > _maximumRecordBytes)
        {
            throw new InvalidDataException("Git status contained a record above the configured limit.");
        }

        var record = bytes[..terminator];
        bytes = bytes[(terminator + 1)..];
        return record;
    }

    private static void ParseHeader(
        ReadOnlySpan<byte> record,
        ref ObjectId? headObjectId,
        ref RefName? headName,
        ref RefName? upstreamName,
        ref int aheadCount,
        ref int behindCount)
    {
        if (record.StartsWith("# branch.oid "u8))
        {
            var value = record[13..];
            if (value.SequenceEqual("(initial)"u8))
            {
                headObjectId = null;
            }
            else if (!ObjectId.TryParseHex(value, out headObjectId))
            {
                throw new InvalidDataException("Git status contained an invalid branch object identifier.");
            }
        }
        else if (record.StartsWith("# branch.head "u8))
        {
            var value = record[14..];
            headName = value.SequenceEqual("(detached)"u8) ? null : RefName.FromBytes(value);
        }
        else if (record.StartsWith("# branch.upstream "u8))
        {
            upstreamName = RefName.FromBytes(record[18..]);
        }
        else if (record.StartsWith("# branch.ab "u8))
        {
            ParseAheadBehind(record[12..], out aheadCount, out behindCount);
        }
    }

    private static RepositoryStatusEntry ParseOrdinary(ReadOnlySpan<byte> record)
    {
        ValidateStatusPrefix(record, (byte)'1');
        var path = GetRemainderAfterSpaces(record, 8);
        return new RepositoryStatusEntry(
            RepositoryStatusEntryKind.Ordinary,
            ParseFileStatus(record[2]),
            ParseFileStatus(record[3]),
            CreatePath(path),
            OriginalPath: null,
            SimilarityPercentage: null,
            IsSubmodule: record[5] == (byte)'S');
    }

    private static RepositoryStatusEntry ParseRenameOrCopy(
        ReadOnlySpan<byte> record,
        ReadOnlySpan<byte> originalPath)
    {
        ValidateStatusPrefix(record, (byte)'2');
        if (originalPath.IsEmpty)
        {
            throw new InvalidDataException("Git status contained an empty rename source path.");
        }

        var scoreField = GetField(record, 8);
        if (scoreField.Length < 2 || scoreField[0] is not ((byte)'R' or (byte)'C') ||
            !TryParseUnsignedDecimal(scoreField[1..], out var score) || score is < 0 or > 100)
        {
            throw new InvalidDataException("Git status contained an invalid rename or copy score.");
        }

        return new RepositoryStatusEntry(
            scoreField[0] == (byte)'R' ? RepositoryStatusEntryKind.Rename : RepositoryStatusEntryKind.Copy,
            ParseFileStatus(record[2]),
            ParseFileStatus(record[3]),
            CreatePath(GetRemainderAfterSpaces(record, 9)),
            CreatePath(originalPath),
            score,
            IsSubmodule: record[5] == (byte)'S');
    }

    private static RepositoryStatusEntry ParseUnmerged(ReadOnlySpan<byte> record)
    {
        ValidateStatusPrefix(record, (byte)'u');
        return new RepositoryStatusEntry(
            RepositoryStatusEntryKind.Unmerged,
            ParseFileStatus(record[2]),
            ParseFileStatus(record[3]),
            CreatePath(GetRemainderAfterSpaces(record, 10)),
            OriginalPath: null,
            SimilarityPercentage: null,
            IsSubmodule: record[5] == (byte)'S');
    }

    private static RepositoryStatusEntry ParseSimple(
        ReadOnlySpan<byte> record,
        RepositoryStatusEntryKind kind,
        GitFileStatus status)
    {
        if (record.Length < 3 || record[1] != (byte)' ')
        {
            throw new InvalidDataException("Git status contained an invalid simple path record.");
        }

        return new RepositoryStatusEntry(
            kind,
            GitFileStatus.Unmodified,
            status,
            CreatePath(record[2..]),
            OriginalPath: null,
            SimilarityPercentage: null,
            IsSubmodule: false);
    }

    private static void ValidateStatusPrefix(ReadOnlySpan<byte> record, byte expectedType)
    {
        if (record.Length < 6 || record[0] != expectedType || record[1] != (byte)' ' ||
            record[4] != (byte)' ')
        {
            throw new InvalidDataException("Git status contained an invalid tracked entry prefix.");
        }
    }

    private static GitFileStatus ParseFileStatus(byte value)
        => value switch
        {
            (byte)'.' => GitFileStatus.Unmodified,
            (byte)'M' => GitFileStatus.Modified,
            (byte)'A' => GitFileStatus.Added,
            (byte)'D' => GitFileStatus.Deleted,
            (byte)'R' => GitFileStatus.Renamed,
            (byte)'C' => GitFileStatus.Copied,
            (byte)'T' => GitFileStatus.TypeChanged,
            (byte)'U' => GitFileStatus.Unmerged,
            _ => throw new InvalidDataException("Git status contained an unknown XY status value."),
        };

    private static ReadOnlySpan<byte> GetRemainderAfterSpaces(ReadOnlySpan<byte> record, int count)
    {
        var offset = 0;
        for (var index = 0; index < count; index++)
        {
            var separator = record[offset..].IndexOf((byte)' ');
            if (separator < 0)
            {
                throw new InvalidDataException("Git status contained too few fixed fields.");
            }

            offset += separator + 1;
        }

        if (offset >= record.Length)
        {
            throw new InvalidDataException("Git status contained an empty path.");
        }

        return record[offset..];
    }

    private static ReadOnlySpan<byte> GetField(ReadOnlySpan<byte> record, int fieldIndex)
    {
        var start = 0;
        for (var index = 0; index < fieldIndex; index++)
        {
            var separator = record[start..].IndexOf((byte)' ');
            if (separator < 0)
            {
                throw new InvalidDataException("Git status contained too few fixed fields.");
            }

            start += separator + 1;
        }

        var end = record[start..].IndexOf((byte)' ');
        if (end <= 0)
        {
            throw new InvalidDataException("Git status contained an empty fixed field.");
        }

        return record.Slice(start, end);
    }

    private static GitPath CreatePath(ReadOnlySpan<byte> bytes)
        => OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath(Encoding.UTF8.GetString(bytes))
            : GitPath.FromUnixBytes(bytes);

    private static void ParseAheadBehind(ReadOnlySpan<byte> value, out int ahead, out int behind)
    {
        var separator = value.IndexOf((byte)' ');
        if (separator < 0 || value.IsEmpty || value[0] != (byte)'+' ||
            separator + 2 >= value.Length || value[separator + 1] != (byte)'-' ||
            !TryParseUnsignedDecimal(value[1..separator], out ahead) ||
            !TryParseUnsignedDecimal(value[(separator + 2)..], out behind))
        {
            throw new InvalidDataException("Git status contained invalid ahead/behind counts.");
        }
    }

    private static bool TryParseUnsignedDecimal(ReadOnlySpan<byte> value, out int result)
    {
        result = 0;
        if (value.IsEmpty)
        {
            return false;
        }

        foreach (var digitByte in value)
        {
            if (digitByte is < (byte)'0' or > (byte)'9')
            {
                return false;
            }

            var digit = digitByte - (byte)'0';
            if (result > (int.MaxValue - digit) / 10)
            {
                return false;
            }

            result = (result * 10) + digit;
        }

        return true;
    }
}
