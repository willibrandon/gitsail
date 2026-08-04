using GitSail.Domain;
using System.Collections.Immutable;
using System.Globalization;

namespace GitSail.Git.Parsing;

/// <summary>
/// Parses explicit double-NUL-terminated stash reflog records without locale assumptions.
/// </summary>
internal sealed class StashCatalogParser
{
    private const int DefaultMaximumRecordBytes = 16 * 1024 * 1024;
    private const int DefaultMaximumEntries = 1_000_000;
    private static ReadOnlySpan<byte> SelectorPrefix => "refs/stash@{"u8;
    private readonly int _maximumRecordBytes;
    private readonly int _maximumEntries;

    /// <summary>
    /// Initializes a bounded parser for Git's explicit stash reflog format.
    /// </summary>
    /// <param name="maximumRecordBytes">The maximum aggregate byte count for one record.</param>
    /// <param name="maximumEntries">The maximum accepted stash entry count.</param>
    internal StashCatalogParser(
        int maximumRecordBytes = DefaultMaximumRecordBytes,
        int maximumEntries = DefaultMaximumEntries)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRecordBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntries);
        _maximumRecordBytes = maximumRecordBytes;
        _maximumEntries = maximumEntries;
    }

    /// <summary>
    /// Parses a complete explicit-format stash reflog byte stream.
    /// </summary>
    /// <param name="bytes">The complete four-field, double-NUL-terminated byte stream.</param>
    /// <returns>The complete ordered immutable stash entries.</returns>
    internal ImmutableArray<StashInfo> Parse(ReadOnlySpan<byte> bytes)
    {
        var entries = ImmutableArray.CreateBuilder<StashInfo>();
        while (!bytes.IsEmpty)
        {
            if (entries.Count >= _maximumEntries)
            {
                throw new InvalidDataException("Git returned more stash entries than the configured limit.");
            }

            var recordStartLength = bytes.Length;
            var objectField = TakeField(ref bytes);
            var selectorField = TakeField(ref bytes);
            var messageField = TakeField(ref bytes);
            var timestampField = TakeField(ref bytes);
            if (bytes.IsEmpty || bytes[0] != 0)
            {
                throw new InvalidDataException("Git stash output ended before its NUL record terminator.");
            }

            bytes = bytes[1..];
            if (recordStartLength - bytes.Length > _maximumRecordBytes)
            {
                throw new InvalidDataException("Git returned a stash record above the configured limit.");
            }

            if (!ObjectId.TryParseHex(objectField, out var objectId))
            {
                throw new InvalidDataException("Git returned an invalid stash object identifier.");
            }

            var expectedIndex = entries.Count;
            var parsedIndex = ParseSelector(selectorField);
            if (parsedIndex != expectedIndex)
            {
                throw new InvalidDataException("Git returned a noncontiguous stash reflog selector order.");
            }

            if (!long.TryParse(
                    timestampField,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var unixSeconds))
            {
                throw new InvalidDataException("Git returned an invalid stash reflog timestamp.");
            }

            DateTimeOffset createdAt;
            try
            {
                createdAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                throw new InvalidDataException("Git returned an out-of-range stash reflog timestamp.", exception);
            }

            entries.Add(new StashInfo(expectedIndex, objectId!, messageField, createdAt));
        }

        return entries.ToImmutable();
    }

    private static ReadOnlySpan<byte> TakeField(ref ReadOnlySpan<byte> bytes)
    {
        var terminator = bytes.IndexOf((byte)0);
        if (terminator < 0)
        {
            throw new InvalidDataException("Git stash output ended before a NUL field terminator.");
        }

        var field = bytes[..terminator];
        bytes = bytes[(terminator + 1)..];
        return field;
    }

    private static int ParseSelector(ReadOnlySpan<byte> selector)
    {
        if (!selector.StartsWith(SelectorPrefix) || selector.Length <= SelectorPrefix.Length + 1 || selector[^1] != (byte)'}')
        {
            throw new InvalidDataException("Git returned an invalid full stash reflog selector.");
        }

        var indexField = selector[SelectorPrefix.Length..^1];
        if (indexField.Length > 1 && indexField[0] == (byte)'0')
        {
            throw new InvalidDataException("Git returned a noncanonical stash reflog selector.");
        }

        if (!int.TryParse(indexField, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 0)
        {
            throw new InvalidDataException("Git returned an invalid stash reflog selector index.");
        }

        return index;
    }
}
