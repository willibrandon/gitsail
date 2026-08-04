using GitSail.Domain;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace GitSail.Git.Parsing;

/// <summary>
/// Parses bounded NUL-delimited long-format Git tree entries without quoting or locale assumptions.
/// </summary>
internal sealed class TreeEntryParser
{
    private const int DefaultMaximumEntryBytes = 16 * 1024 * 1024;
    private const int DefaultMaximumEntryCount = 1_000_000;
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly int _maximumEntryBytes;
    private readonly int _maximumEntryCount;

    /// <summary>
    /// Initializes a bounded exact tree-entry parser.
    /// </summary>
    /// <param name="maximumEntryBytes">The maximum byte count for one tree entry.</param>
    /// <param name="maximumEntryCount">The maximum accepted tree entry count.</param>
    internal TreeEntryParser(
        int maximumEntryBytes = DefaultMaximumEntryBytes,
        int maximumEntryCount = DefaultMaximumEntryCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntryBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntryCount);
        _maximumEntryBytes = maximumEntryBytes;
        _maximumEntryCount = maximumEntryCount;
    }

    /// <summary>
    /// Parses a complete long-format <c>git ls-tree -z</c> byte stream.
    /// </summary>
    /// <param name="bytes">The complete NUL-delimited tree output.</param>
    /// <returns>The ordered immutable exact tree entries.</returns>
    internal ImmutableArray<TreeEntry> Parse(ReadOnlySpan<byte> bytes)
    {
        var entries = ImmutableArray.CreateBuilder<TreeEntry>();
        while (!bytes.IsEmpty)
        {
            if (entries.Count >= _maximumEntryCount)
            {
                throw new InvalidDataException("Git returned more tree entries than the configured limit.");
            }

            var terminator = bytes.IndexOf((byte)0);
            if (terminator < 0)
            {
                throw new InvalidDataException("Git tree output ended before its NUL record terminator.");
            }

            var record = bytes[..terminator];
            bytes = bytes[(terminator + 1)..];
            if (record.IsEmpty || record.Length > _maximumEntryBytes)
            {
                throw new InvalidDataException("Git returned an empty or oversized tree entry.");
            }

            entries.Add(ParseEntry(record));
        }

        return entries.ToImmutable();
    }

    private static TreeEntry ParseEntry(ReadOnlySpan<byte> record)
    {
        var nameSeparator = record.IndexOf((byte)'\t');
        if (nameSeparator < 0 || nameSeparator == record.Length - 1)
        {
            throw new InvalidDataException("Git returned a tree entry without its exact name.");
        }

        var metadata = record[..nameSeparator];
        var name = CreatePath(record[(nameSeparator + 1)..]);
        var modeField = TakeSpaceField(ref metadata);
        var typeField = TakeSpaceField(ref metadata);
        var objectField = TakeSpaceField(ref metadata);
        metadata = TrimAsciiSpaces(metadata);
        if (metadata.IsEmpty)
        {
            throw new InvalidDataException("Git returned a tree entry without its size field.");
        }

        if (!ObjectId.TryParseHex(objectField, out var objectId))
        {
            throw new InvalidDataException("Git returned an invalid tree entry object identifier.");
        }

        var mode = ParseAscii(modeField, "tree entry mode");
        var kind = ParseKind(modeField, typeField);
        long? size = null;
        if (!metadata.SequenceEqual("-"u8))
        {
            if (!long.TryParse(
                    metadata,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var parsedSize) ||
                parsedSize < 0)
            {
                throw new InvalidDataException("Git returned an invalid tree entry size.");
            }

            size = parsedSize;
        }

        if (kind is TreeEntryKind.Tree or TreeEntryKind.GitLink ? size is not null : size is null)
        {
            throw new InvalidDataException("Git returned a tree entry with a size that conflicts with its kind.");
        }

        return new TreeEntry(kind, mode, objectId!, size, name);
    }

    private static TreeEntryKind ParseKind(ReadOnlySpan<byte> mode, ReadOnlySpan<byte> type)
    {
        if (mode.SequenceEqual("040000"u8) && type.SequenceEqual("tree"u8))
        {
            return TreeEntryKind.Tree;
        }

        if (mode.SequenceEqual("100644"u8) && type.SequenceEqual("blob"u8))
        {
            return TreeEntryKind.RegularFile;
        }

        if (mode.SequenceEqual("100755"u8) && type.SequenceEqual("blob"u8))
        {
            return TreeEntryKind.ExecutableFile;
        }

        if (mode.SequenceEqual("120000"u8) && type.SequenceEqual("blob"u8))
        {
            return TreeEntryKind.SymbolicLink;
        }

        if (mode.SequenceEqual("160000"u8) && type.SequenceEqual("commit"u8))
        {
            return TreeEntryKind.GitLink;
        }

        throw new InvalidDataException("Git returned an unsupported or inconsistent tree entry mode and type.");
    }

    private static ReadOnlySpan<byte> TakeSpaceField(ref ReadOnlySpan<byte> metadata)
    {
        metadata = TrimAsciiSpaces(metadata);
        var separator = metadata.IndexOf((byte)' ');
        if (separator <= 0)
        {
            throw new InvalidDataException("Git returned incomplete tree entry metadata.");
        }

        var field = metadata[..separator];
        metadata = metadata[(separator + 1)..];
        return field;
    }

    private static ReadOnlySpan<byte> TrimAsciiSpaces(ReadOnlySpan<byte> value)
    {
        var start = 0;
        while (start < value.Length && value[start] == (byte)' ')
        {
            start++;
        }

        var end = value.Length;
        while (end > start && value[end - 1] == (byte)' ')
        {
            end--;
        }

        return value[start..end];
    }

    private static GitPath CreatePath(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return OperatingSystem.IsWindows()
                ? GitPath.FromWindowsPath(s_strictUtf8.GetString(bytes))
                : GitPath.FromUnixBytes(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("A Windows tree entry name is not valid UTF-8.", exception);
        }
    }

    private static string ParseAscii(ReadOnlySpan<byte> value, string fieldName)
    {
        foreach (var item in value)
        {
            if (item > 0x7f)
            {
                throw new InvalidDataException($"Git returned a non-ASCII {fieldName}.");
            }
        }

        return Encoding.ASCII.GetString(value);
    }
}
