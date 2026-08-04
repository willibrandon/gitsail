using GitSail.Domain;
using System.Collections.Immutable;
using System.Globalization;

namespace GitSail.Git.Parsing;

/// <summary>
/// Parses bounded line-framed records emitted by <c>git blame --incremental</c>.
/// </summary>
internal sealed class BlameIncrementalParser
{
    private const int DefaultMaximumRecordCount = 10_000_000;
    private const int DefaultMaximumLineBytes = 16 * 1024 * 1024;
    private readonly int _maximumRecordCount;
    private readonly int _maximumLineBytes;

    /// <summary>
    /// Initializes a bounded incremental-blame parser.
    /// </summary>
    /// <param name="maximumRecordCount">The maximum accepted attribution count.</param>
    /// <param name="maximumLineBytes">The maximum accepted protocol-line length.</param>
    internal BlameIncrementalParser(
        int maximumRecordCount = DefaultMaximumRecordCount,
        int maximumLineBytes = DefaultMaximumLineBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRecordCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLineBytes);
        _maximumRecordCount = maximumRecordCount;
        _maximumLineBytes = maximumLineBytes;
    }

    /// <summary>
    /// Parses every complete group and expands it into ordered per-result-line metadata.
    /// </summary>
    /// <param name="bytes">The complete bounded incremental-blame byte stream.</param>
    /// <returns>The ordered immutable line attributions.</returns>
    internal ImmutableArray<BlameAttribution> Parse(ReadOnlySpan<byte> bytes)
    {
        var attributions = ImmutableArray.CreateBuilder<BlameAttribution>();
        var commits = new Dictionary<string, BlameCommit>(StringComparer.Ordinal);
        var boundaryCommits = new HashSet<string>(StringComparer.Ordinal);
        while (!bytes.IsEmpty)
        {
            var header = TakeLine(ref bytes);
            ParseHeader(header, out var objectId, out var sourceLine, out var resultLine, out var lineCount);
            var key = objectId.ToString();
            byte[]? authorName = null;
            byte[]? authorEmail = null;
            long? authorTime = null;
            string? authorTimeZone = null;
            byte[]? summary = null;
            BlamePrevious? previous = null;
            GitPath? sourcePath = null;
            var boundary = false;
            while (sourcePath is null)
            {
                if (bytes.IsEmpty)
                {
                    throw new InvalidDataException("Git blame output ended before a filename field.");
                }

                var field = TakeLine(ref bytes);
                if (field.StartsWith("author "u8))
                {
                    authorName = field[7..].ToArray();
                }
                else if (field.StartsWith("author-mail "u8))
                {
                    authorEmail = field[12..].ToArray();
                }
                else if (field.StartsWith("author-time "u8))
                {
                    authorTime = ParseInt64(field[12..], "author timestamp");
                }
                else if (field.StartsWith("author-tz "u8))
                {
                    authorTimeZone = ParseTimeZone(field[10..]);
                }
                else if (field.StartsWith("summary "u8))
                {
                    summary = field[8..].ToArray();
                }
                else if (field.SequenceEqual("boundary"u8))
                {
                    boundary = true;
                }
                else if (field.StartsWith("previous "u8))
                {
                    previous = ParsePrevious(field[9..], objectId.Format);
                }
                else if (field.StartsWith("filename "u8))
                {
                    sourcePath = BlamePathParser.Parse(field[9..]);
                }
                else if (!IsIgnoredCommitterField(field))
                {
                    throw new InvalidDataException("Git blame returned an unknown incremental metadata field.");
                }
            }

            if (!commits.TryGetValue(key, out var commit))
            {
                if (authorName is null || authorEmail is null || authorTime is null ||
                    authorTimeZone is null || summary is null)
                {
                    throw new InvalidDataException("Git blame omitted required metadata for a new commit identity.");
                }

                commit = new BlameCommit(
                    objectId,
                    authorName,
                    authorEmail,
                    DateTimeOffset.FromUnixTimeSeconds(authorTime.Value),
                    authorTimeZone,
                    summary);
                commits.Add(key, commit);
                if (boundary)
                {
                    boundaryCommits.Add(key);
                }
            }
            else if (authorName is not null || authorEmail is not null || authorTime is not null ||
                     authorTimeZone is not null || summary is not null)
            {
                throw new InvalidDataException("Git blame repeated only part of cached commit metadata.");
            }

            boundary |= boundaryCommits.Contains(key);
            if (lineCount > _maximumRecordCount - attributions.Count)
            {
                throw new InvalidDataException("Git blame returned more line records than the configured limit.");
            }

            for (var offset = 0; offset < lineCount; offset++)
            {
                attributions.Add(new BlameAttribution(
                    checked(resultLine + offset),
                    checked(sourceLine + offset),
                    commit,
                    sourcePath,
                    previous,
                    boundary));
            }
        }

        return [.. attributions.OrderBy(static attribution => attribution.ResultLineNumber)];
    }

    private static void ParseHeader(
        ReadOnlySpan<byte> header,
        out ObjectId objectId,
        out int sourceLine,
        out int resultLine,
        out int lineCount)
    {
        var objectField = TakeHeaderField(ref header);
        var sourceField = TakeHeaderField(ref header);
        var resultField = TakeHeaderField(ref header);
        var countField = TakeHeaderField(ref header);
        if (!header.IsEmpty || !ObjectId.TryParseHex(objectField, out var parsedObjectId))
        {
            throw new InvalidDataException("Git blame returned an invalid group header.");
        }

        sourceLine = ParsePositiveInt32(sourceField, "source line");
        resultLine = ParsePositiveInt32(resultField, "result line");
        lineCount = ParsePositiveInt32(countField, "line count");
        objectId = parsedObjectId!;
    }

    private static ReadOnlySpan<byte> TakeHeaderField(ref ReadOnlySpan<byte> header)
    {
        if (header.IsEmpty || header[0] == (byte)' ')
        {
            throw new InvalidDataException("Git blame returned an invalid group header.");
        }

        var separator = header.IndexOf((byte)' ');
        if (separator < 0)
        {
            var final = header;
            header = [];
            return final;
        }

        var field = header[..separator];
        header = header[(separator + 1)..];
        return field;
    }

    private static BlamePrevious ParsePrevious(
        ReadOnlySpan<byte> field,
        RepositoryObjectFormat expectedFormat)
    {
        var separator = field.IndexOf((byte)' ');
        if (separator <= 0 || separator == field.Length - 1 ||
            !ObjectId.TryParseHex(field[..separator], out var objectId) ||
            objectId!.Format != expectedFormat)
        {
            throw new InvalidDataException("Git blame returned an invalid previous-origin field.");
        }

        return new BlamePrevious(objectId, BlamePathParser.Parse(field[(separator + 1)..]));
    }

    private static bool IsIgnoredCommitterField(ReadOnlySpan<byte> field)
        => field.StartsWith("committer "u8) ||
            field.StartsWith("committer-mail "u8) ||
            field.StartsWith("committer-time "u8) ||
            field.StartsWith("committer-tz "u8);

    private ReadOnlySpan<byte> TakeLine(ref ReadOnlySpan<byte> bytes)
    {
        var terminator = bytes.IndexOf((byte)'\n');
        if (terminator < 0)
        {
            throw new InvalidDataException("Git blame output ended before a line terminator.");
        }

        if (terminator > _maximumLineBytes)
        {
            throw new InvalidDataException("Git blame returned a protocol line above the configured limit.");
        }

        var line = bytes[..terminator];
        bytes = bytes[(terminator + 1)..];
        return !line.IsEmpty && line[^1] == (byte)'\r' ? line[..^1] : line;
    }

    private static int ParsePositiveInt32(ReadOnlySpan<byte> field, string description)
    {
        if (!int.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            throw new InvalidDataException($"Git blame returned an invalid {description}.");
        }

        return value;
    }

    private static long ParseInt64(ReadOnlySpan<byte> field, string description)
    {
        if (!long.TryParse(field, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
        {
            throw new InvalidDataException($"Git blame returned an invalid {description}.");
        }

        return value;
    }

    private static string ParseTimeZone(ReadOnlySpan<byte> field)
    {
        if (field.Length != 5 || field[0] is not ((byte)'+' or (byte)'-') ||
            !int.TryParse(field[1..3], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
            !int.TryParse(field[3..], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            hours > 23 || minutes > 59)
        {
            throw new InvalidDataException("Git blame returned an invalid author time zone.");
        }

        return System.Text.Encoding.ASCII.GetString(field);
    }
}
