using GitSail.Domain;
using GitSail.Git.Execution;
using System.Buffers;
using System.Collections.Immutable;

namespace GitSail.Git.Parsing;

/// <summary>
/// Builds a bounded file-level index over exact raw patch bytes without decoding content.
/// </summary>
internal static class RawDiffParser
{
    private const int MaximumLineBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Parses one complete raw diff spool into file offsets and exact side paths.
    /// </summary>
    /// <param name="spool">The complete seekable raw diff byte spool.</param>
    /// <param name="generation">The repository generation that produced the bytes.</param>
    /// <returns>The immutable file-level raw diff index.</returns>
    internal static RawDiffIndex Parse(RawByteSpool spool, OperationGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(spool);
        var metadata = RawDiffMetadataParser.Parse(spool);
        using var stream = spool.OpenRead();
        stream.Position = metadata.PatchOffset;
        var files = ImmutableArray.CreateBuilder<RawDiffFile>();
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var line = new ArrayBufferWriter<byte>();
        var streamOffset = metadata.PatchOffset;
        var lineOffset = metadata.PatchOffset;
        var metadataIndex = 0;
        var currentOffset = -1L;
        GitPath? oldPath = null;
        GitPath? newPath = null;
        var hasHunks = false;
        var isBinary = false;
        try
        {
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                var consumed = 0;
                while (consumed < read)
                {
                    var remaining = buffer.AsSpan(consumed, read - consumed);
                    var newline = remaining.IndexOf((byte)'\n');
                    if (newline < 0)
                    {
                        AppendLine(line, remaining);
                        consumed = read;
                        continue;
                    }

                    var segment = remaining[..newline];
                    if (line.WrittenCount == 0)
                    {
                        ProcessLine(
                            TrimCarriageReturn(segment),
                            lineOffset,
                            files,
                            metadata.Paths,
                            ref metadataIndex,
                            ref currentOffset,
                            ref oldPath,
                            ref newPath,
                            ref hasHunks,
                            ref isBinary);
                    }
                    else
                    {
                        AppendLine(line, segment);
                        ProcessLine(
                            TrimCarriageReturn(line.WrittenSpan),
                            lineOffset,
                            files,
                            metadata.Paths,
                            ref metadataIndex,
                            ref currentOffset,
                            ref oldPath,
                            ref newPath,
                            ref hasHunks,
                            ref isBinary);
                        line = new ArrayBufferWriter<byte>();
                    }

                    consumed += newline + 1;
                    lineOffset = streamOffset + consumed;
                }

                streamOffset += read;
            }

            if (line.WrittenCount > 0)
            {
                ProcessLine(
                    TrimCarriageReturn(line.WrittenSpan),
                    lineOffset,
                    files,
                    metadata.Paths,
                    ref metadataIndex,
                    ref currentOffset,
                    ref oldPath,
                    ref newPath,
                    ref hasHunks,
                    ref isBinary);
            }

            CompleteCurrentFile(
                spool.Length,
                files,
                currentOffset,
                oldPath,
                newPath,
                hasHunks,
                isBinary);
            if (metadataIndex != metadata.Paths.Length)
            {
                throw new InvalidDataException("Raw diff metadata and patch file counts did not match.");
            }

            return new RawDiffIndex(generation, files.ToImmutable());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ProcessLine(
        ReadOnlySpan<byte> line,
        long lineOffset,
        ImmutableArray<RawDiffFile>.Builder files,
        ImmutableArray<(GitPath OldPath, GitPath NewPath)> metadataPaths,
        ref int metadataIndex,
        ref long currentOffset,
        ref GitPath? oldPath,
        ref GitPath? newPath,
        ref bool hasHunks,
        ref bool isBinary)
    {
        if (line.StartsWith("diff --git "u8))
        {
            CompleteCurrentFile(
                lineOffset,
                files,
                currentOffset,
                oldPath,
                newPath,
                hasHunks,
                isBinary);
            if (metadataPaths.IsEmpty)
            {
                (oldPath, newPath) = GitQuotedPathParser.ParseDiffHeader(line);
            }
            else
            {
                if (metadataIndex >= metadataPaths.Length)
                {
                    throw new InvalidDataException("Raw diff patch contained more files than its metadata.");
                }

                (oldPath, newPath) = metadataPaths[metadataIndex];
                metadataIndex++;
            }
            currentOffset = lineOffset;
            hasHunks = false;
            isBinary = false;
            return;
        }

        if (currentOffset < 0)
        {
            if (!line.IsEmpty)
            {
                throw new InvalidDataException("Raw diff output contained data before its first file header.");
            }

            return;
        }

        hasHunks |= line.StartsWith("@@ "u8);
        isBinary |= line.SequenceEqual("GIT binary patch"u8) || line.StartsWith("Binary files "u8);
    }

    private static void CompleteCurrentFile(
        long endOffset,
        ImmutableArray<RawDiffFile>.Builder files,
        long currentOffset,
        GitPath? oldPath,
        GitPath? newPath,
        bool hasHunks,
        bool isBinary)
    {
        if (currentOffset < 0)
        {
            return;
        }

        if (oldPath is null || newPath is null || endOffset <= currentOffset)
        {
            throw new InvalidDataException("Raw diff output contained an incomplete file patch.");
        }

        files.Add(new RawDiffFile(
            oldPath,
            newPath,
            currentOffset,
            endOffset - currentOffset,
            hasHunks,
            isBinary));
    }

    private static void AppendLine(ArrayBufferWriter<byte> line, ReadOnlySpan<byte> bytes)
    {
        if (line.WrittenCount > MaximumLineBytes - bytes.Length)
        {
            throw new InvalidDataException("A raw diff line exceeded the configured byte limit.");
        }

        line.Write(bytes);
    }

    private static ReadOnlySpan<byte> TrimCarriageReturn(ReadOnlySpan<byte> line)
        => !line.IsEmpty && line[^1] == (byte)'\r' ? line[..^1] : line;
}
