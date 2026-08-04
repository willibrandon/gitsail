using GitSail.Domain;
using GitSail.Git.Execution;
using System.Buffers;
using System.Collections.Immutable;

namespace GitSail.Git.Parsing;

/// <summary>
/// Parses the NUL-delimited raw metadata prefix emitted before a combined patch stream.
/// </summary>
internal static class RawDiffMetadataParser
{
    private const int MaximumFieldBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Reads exact path pairs from a raw metadata prefix and locates the following patch bytes.
    /// </summary>
    /// <param name="spool">The complete combined raw-metadata and patch spool.</param>
    /// <returns>The patch offset and ordered exact path pairs.</returns>
    internal static (long PatchOffset, ImmutableArray<(GitPath OldPath, GitPath NewPath)> Paths) Parse(
        RawByteSpool spool)
    {
        ArgumentNullException.ThrowIfNull(spool);
        using var stream = spool.OpenRead();
        var first = stream.ReadByte();
        if (first < 0)
        {
            return (0, []);
        }

        if (first != (byte)':')
        {
            return (0, []);
        }

        stream.Position = 0;
        var paths = ImmutableArray.CreateBuilder<(GitPath OldPath, GitPath NewPath)>();
        var field = new ArrayBufferWriter<byte>();
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var expectingHeader = true;
        var remainingPaths = 0;
        GitPath? oldPath = null;
        var streamOffset = 0L;
        try
        {
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    throw new InvalidDataException("Raw diff metadata ended before its patch separator.");
                }

                var consumed = 0;
                while (consumed < read)
                {
                    var remaining = buffer.AsSpan(consumed, read - consumed);
                    var terminator = remaining.IndexOf((byte)0);
                    if (terminator < 0)
                    {
                        AppendField(field, remaining);
                        consumed = read;
                        continue;
                    }

                    AppendField(field, remaining[..terminator]);
                    consumed += terminator + 1;
                    if (field.WrittenCount == 0)
                    {
                        if (!expectingHeader || paths.Count == 0)
                        {
                            throw new InvalidDataException("Raw diff metadata contained an unexpected empty field.");
                        }

                        return (streamOffset + consumed, paths.ToImmutable());
                    }

                    if (expectingHeader)
                    {
                        remainingPaths = ParsePathCount(field.WrittenSpan);
                        expectingHeader = false;
                    }
                    else
                    {
                        var path = GitQuotedPathParser.ParseRawPath(field.WrittenSpan);
                        if (remainingPaths == 2)
                        {
                            oldPath = path;
                            remainingPaths = 1;
                        }
                        else
                        {
                            paths.Add((oldPath ?? path, path));
                            oldPath = null;
                            remainingPaths = 0;
                            expectingHeader = true;
                        }
                    }

                    field = new ArrayBufferWriter<byte>();
                }

                streamOffset += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int ParsePathCount(ReadOnlySpan<byte> header)
    {
        if (header.IsEmpty || header[0] != (byte)':')
        {
            throw new InvalidDataException("Raw diff metadata contained an invalid record header.");
        }

        var statusSeparator = header.LastIndexOf((byte)' ');
        if (statusSeparator < 0 || statusSeparator == header.Length - 1)
        {
            throw new InvalidDataException("Raw diff metadata omitted its status field.");
        }

        var statusField = header[(statusSeparator + 1)..];
        var status = statusField[0];
        if (status is (byte)'C' or (byte)'R')
        {
            if (statusField.Length < 2 || !ContainsOnlyDecimalDigits(statusField[1..]))
            {
                throw new InvalidDataException("Raw diff metadata contained an invalid similarity score.");
            }

            return 2;
        }

        if (statusField.Length != 1 ||
            status is not ((byte)'A') and
                not ((byte)'D') and
                not ((byte)'M') and
                not ((byte)'T') and
                not ((byte)'U') and
                not ((byte)'X') and
                not ((byte)'B'))
        {
            throw new InvalidDataException("Raw diff metadata contained an unknown status field.");
        }

        return 1;
    }

    private static bool ContainsOnlyDecimalDigits(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (value is < (byte)'0' or > (byte)'9')
            {
                return false;
            }
        }

        return true;
    }

    private static void AppendField(ArrayBufferWriter<byte> field, ReadOnlySpan<byte> bytes)
    {
        if (field.WrittenCount > MaximumFieldBytes - bytes.Length)
        {
            throw new InvalidDataException("A raw diff metadata field exceeded the configured byte limit.");
        }

        field.Write(bytes);
    }
}
