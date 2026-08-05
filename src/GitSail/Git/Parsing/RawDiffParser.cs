using GitSail.Domain;
using GitSail.Git.Execution;
using System.Buffers;
using System.Collections.Immutable;
using System.Text;

namespace GitSail.Git.Parsing;

/// <summary>
/// Builds a bounded file, hunk, and line index over exact raw patch bytes without decoding content.
/// </summary>
internal static class RawDiffParser
{
    private const int MaximumLineBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Parses one complete raw diff spool into exact paths and nested byte-level patch indexes.
    /// </summary>
    /// <param name="spool">The complete seekable raw diff byte spool.</param>
    /// <param name="generation">The repository generation that produced the bytes.</param>
    /// <returns>The immutable byte-level raw diff index.</returns>
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
        RawPatchIndexBuilder? patchIndexBuilder = null;
        var isBinary = false;
        var skippingCombinedPatch = false;
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
                            segment.Length + 1,
                            files,
                            metadata.Paths,
                            ref metadataIndex,
                            ref currentOffset,
                            ref oldPath,
                            ref newPath,
                            ref patchIndexBuilder,
                            ref isBinary,
                            ref skippingCombinedPatch);
                    }
                    else
                    {
                        AppendLine(line, segment);
                        ProcessLine(
                            TrimCarriageReturn(line.WrittenSpan),
                            lineOffset,
                            line.WrittenCount + 1,
                            files,
                            metadata.Paths,
                            ref metadataIndex,
                            ref currentOffset,
                            ref oldPath,
                            ref newPath,
                            ref patchIndexBuilder,
                            ref isBinary,
                            ref skippingCombinedPatch);
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
                    line.WrittenCount,
                    files,
                    metadata.Paths,
                    ref metadataIndex,
                    ref currentOffset,
                    ref oldPath,
                    ref newPath,
                    ref patchIndexBuilder,
                    ref isBinary,
                    ref skippingCombinedPatch);
            }

            CompleteCurrentFile(
                spool.Length,
                files,
                currentOffset,
                oldPath,
                newPath,
                patchIndexBuilder,
                isBinary);
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
        int totalLength,
        ImmutableArray<RawDiffFile>.Builder files,
        ImmutableArray<(
            GitPath OldPath,
            GitPath NewPath,
            bool IsRawOnly,
            bool IsCombined)> metadataPaths,
        ref int metadataIndex,
        ref long currentOffset,
        ref GitPath? oldPath,
        ref GitPath? newPath,
        ref RawPatchIndexBuilder? patchIndexBuilder,
        ref bool isBinary,
        ref bool skippingCombinedPatch)
    {
        if (line.StartsWith("diff --git "u8))
        {
            CompleteCurrentFile(
                lineOffset,
                files,
                currentOffset,
                oldPath,
                newPath,
                patchIndexBuilder,
                isBinary);
            if (metadataPaths.IsEmpty)
            {
                (oldPath, newPath) = GitQuotedPathParser.ParseDiffHeader(line);
            }
            else
            {
                SkipMetadataWithoutRequiredPatch(metadataPaths, ref metadataIndex, includeCombined: true);
                if (metadataIndex >= metadataPaths.Length)
                {
                    throw new InvalidDataException("Raw diff patch contained more files than its metadata.");
                }

                if (metadataPaths[metadataIndex].IsCombined)
                {
                    throw new InvalidDataException("A unified patch was paired with combined-diff metadata.");
                }

                var matchingIndex = FindMatchingMetadata(metadataPaths, metadataIndex, line);
                if (matchingIndex < 0)
                {
                    throw new InvalidDataException(
                        "A raw diff patch header did not match its remaining exact-path metadata.");
                }

                metadataIndex = matchingIndex;
                oldPath = metadataPaths[metadataIndex].OldPath;
                newPath = metadataPaths[metadataIndex].NewPath;
                metadataIndex++;
            }
            currentOffset = lineOffset;
            patchIndexBuilder = new RawPatchIndexBuilder(lineOffset);
            patchIndexBuilder.ProcessLine(line, lineOffset, totalLength);
            isBinary = false;
            skippingCombinedPatch = false;
            return;
        }

        if (line.StartsWith("diff --cc "u8) || line.StartsWith("diff --combined "u8))
        {
            CompleteCurrentFile(
                lineOffset,
                files,
                currentOffset,
                oldPath,
                newPath,
                patchIndexBuilder,
                isBinary);
            SkipMetadataWithoutRequiredPatch(metadataPaths, ref metadataIndex, includeCombined: false);
            if (metadataIndex >= metadataPaths.Length || !metadataPaths[metadataIndex].IsCombined)
            {
                throw new InvalidDataException("Raw combined diff had no matching exact-path metadata.");
            }

            metadataIndex++;
            currentOffset = -1;
            oldPath = null;
            newPath = null;
            patchIndexBuilder = null;
            isBinary = false;
            skippingCombinedPatch = true;
            return;
        }

        if (line.StartsWith("* Unmerged path "u8))
        {
            if (metadataIndex >= metadataPaths.Length ||
                (!metadataPaths[metadataIndex].IsRawOnly && !metadataPaths[metadataIndex].IsCombined))
            {
                throw new InvalidDataException("An unmerged-path patch notice had no matching raw metadata.");
            }

            metadataIndex++;
            skippingCombinedPatch = true;
            return;
        }

        if (currentOffset < 0)
        {
            if (!line.IsEmpty && !skippingCombinedPatch)
            {
                throw new InvalidDataException(
                    $"Raw diff output contained data before its first file header " +
                    $"(prefix {Convert.ToHexString(line[..Math.Min(line.Length, 32)])}).");
            }

            return;
        }

        patchIndexBuilder!.ProcessLine(line, lineOffset, totalLength);
        isBinary |= line.SequenceEqual("GIT binary patch"u8) || line.StartsWith("Binary files "u8);
    }

    private static int FindMatchingMetadata(
        ImmutableArray<(
            GitPath OldPath,
            GitPath NewPath,
            bool IsRawOnly,
            bool IsCombined)> metadataPaths,
        int startIndex,
        ReadOnlySpan<byte> header)
    {
        for (var index = startIndex; index < metadataPaths.Length; index++)
        {
            var candidate = metadataPaths[index];
            if (!candidate.IsRawOnly &&
                !candidate.IsCombined &&
                MatchesDiffHeader(header, candidate.OldPath, candidate.NewPath))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool MatchesDiffHeader(
        ReadOnlySpan<byte> header,
        GitPath oldPath,
        GitPath newPath)
    {
        var expected = new ArrayBufferWriter<byte>();
        expected.Write("diff --git "u8);
        AppendQuotedPath(expected, "a/"u8, GetPathBytes(oldPath));
        expected.Write(" "u8);
        AppendQuotedPath(expected, "b/"u8, GetPathBytes(newPath));
        return header.SequenceEqual(expected.WrittenSpan);
    }

    private static byte[] GetPathBytes(GitPath path)
        => OperatingSystem.IsWindows()
            ? Encoding.UTF8.GetBytes(path.GetWindowsPath())
            : path.GetUnixBytes().ToArray();

    private static void AppendQuotedPath(
        ArrayBufferWriter<byte> output,
        ReadOnlySpan<byte> prefix,
        ReadOnlySpan<byte> path)
    {
        var quote = path.IndexOfAnyInRange((byte)0, (byte)31) >= 0 ||
            path.IndexOfAnyInRange((byte)127, byte.MaxValue) >= 0 ||
            path.ContainsAny((byte)'"', (byte)'\\');
        if (!quote)
        {
            output.Write(prefix);
            output.Write(path);
            return;
        }

        output.Write("\""u8);
        output.Write(prefix);
        foreach (var value in path)
        {
            switch (value)
            {
                case (byte)'\a':
                    output.Write("\\a"u8);
                    break;
                case (byte)'\b':
                    output.Write("\\b"u8);
                    break;
                case (byte)'\t':
                    output.Write("\\t"u8);
                    break;
                case (byte)'\n':
                    output.Write("\\n"u8);
                    break;
                case (byte)'\v':
                    output.Write("\\v"u8);
                    break;
                case (byte)'\f':
                    output.Write("\\f"u8);
                    break;
                case (byte)'\r':
                    output.Write("\\r"u8);
                    break;
                case (byte)'"':
                    output.Write("\\\""u8);
                    break;
                case (byte)'\\':
                    output.Write("\\\\"u8);
                    break;
                case < 32 or >= 127:
                    output.Write(
                    [
                        (byte)'\\',
                        (byte)('0' + ((value >> 6) & 7)),
                        (byte)('0' + ((value >> 3) & 7)),
                        (byte)('0' + (value & 7)),
                    ]);
                    break;
                default:
                    output.Write([value]);
                    break;
            }
        }

        output.Write("\""u8);
    }

    private static void SkipMetadataWithoutRequiredPatch(
        ImmutableArray<(
            GitPath OldPath,
            GitPath NewPath,
            bool IsRawOnly,
            bool IsCombined)> metadataPaths,
        ref int metadataIndex,
        bool includeCombined)
    {
        while (metadataIndex < metadataPaths.Length &&
            (metadataPaths[metadataIndex].IsRawOnly ||
                (includeCombined && metadataPaths[metadataIndex].IsCombined)))
        {
            metadataIndex++;
        }
    }

    private static void CompleteCurrentFile(
        long endOffset,
        ImmutableArray<RawDiffFile>.Builder files,
        long currentOffset,
        GitPath? oldPath,
        GitPath? newPath,
        RawPatchIndexBuilder? patchIndexBuilder,
        bool isBinary)
    {
        if (currentOffset < 0)
        {
            return;
        }

        if (oldPath is null || newPath is null || patchIndexBuilder is null || endOffset <= currentOffset)
        {
            throw new InvalidDataException("Raw diff output contained an incomplete file patch.");
        }

        var patchIndex = patchIndexBuilder.Complete(endOffset);

        files.Add(new RawDiffFile(
            oldPath,
            newPath,
            currentOffset,
            endOffset - currentOffset,
            patchIndex,
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
