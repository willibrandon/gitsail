using GitSail.Domain;
using System.Collections.Immutable;
using System.Text;

namespace GitSail.Git.Parsing;

/// <summary>
/// Reads bounded literal native pathspec records from a file or standard input.
/// </summary>
internal static class PathspecFileReader
{
    private const int MaximumInputBytes = 64 * 1024 * 1024;
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// Reads complete line- or NUL-delimited literal pathspec records without shell interpretation.
    /// </summary>
    /// <param name="file">The input file path or <c>-</c> for standard input.</param>
    /// <param name="nulDelimited">Whether NUL delimiters and a final NUL terminator are required.</param>
    /// <param name="cancellationToken">Signals bounded input cancellation.</param>
    /// <returns>The exact native pathspec records.</returns>
    internal static async Task<ImmutableArray<GitPath>> ReadAsync(
        string file,
        bool nulDelimited,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        if (file.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A pathspec input file name cannot contain NUL.", nameof(file));
        }

        if (string.Equals(file, "-", StringComparison.Ordinal))
        {
            var standardInput = Console.OpenStandardInput();
            var bytes = await ReadBoundedAsync(standardInput, cancellationToken).ConfigureAwait(false);
            return Parse(bytes, nulDelimited);
        }

        await using var stream = new FileStream(
            Path.GetFullPath(file, Environment.CurrentDirectory),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var contents = await ReadBoundedAsync(stream, cancellationToken).ConfigureAwait(false);
        return Parse(contents, nulDelimited);
    }

    /// <summary>
    /// Parses complete bounded pathspec input bytes into exact native records.
    /// </summary>
    /// <param name="bytes">The complete bounded input bytes.</param>
    /// <param name="nulDelimited">Whether NUL delimiters and a final NUL terminator are required.</param>
    /// <returns>The exact native pathspec records.</returns>
    internal static ImmutableArray<GitPath> Parse(ReadOnlySpan<byte> bytes, bool nulDelimited)
        => nulDelimited ? ParseNulRecords(bytes) : ParseLineRecords(bytes);

    private static ImmutableArray<GitPath> ParseNulRecords(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return [];
        }

        if (bytes[^1] != 0)
        {
            throw new InvalidDataException("NUL-delimited pathspec input must end with a NUL record terminator.");
        }

        var paths = ImmutableArray.CreateBuilder<GitPath>();
        while (!bytes.IsEmpty)
        {
            var terminator = bytes.IndexOf((byte)0);
            if (terminator < 0)
            {
                throw new InvalidDataException("Pathspec input ended before its NUL record terminator.");
            }

            AddPath(paths, bytes[..terminator]);
            bytes = bytes[(terminator + 1)..];
        }

        return paths.ToImmutable();
    }

    private static ImmutableArray<GitPath> ParseLineRecords(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Contains((byte)0))
        {
            throw new InvalidDataException("Line-delimited pathspec input cannot contain NUL bytes.");
        }

        var paths = ImmutableArray.CreateBuilder<GitPath>();
        while (!bytes.IsEmpty)
        {
            var terminator = bytes.IndexOf((byte)'\n');
            var record = terminator < 0 ? bytes : bytes[..terminator];
            if (!record.IsEmpty && record[^1] == (byte)'\r')
            {
                record = record[..^1];
            }

            AddPath(paths, record);
            if (terminator < 0)
            {
                break;
            }

            bytes = bytes[(terminator + 1)..];
            if (bytes.IsEmpty)
            {
                break;
            }
        }

        return paths.ToImmutable();
    }

    private static void AddPath(ImmutableArray<GitPath>.Builder paths, ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            throw new InvalidDataException("Pathspec input cannot contain an empty record.");
        }

        try
        {
            paths.Add(OperatingSystem.IsWindows()
                ? GitPath.FromWindowsPath(s_strictUtf8.GetString(bytes))
                : GitPath.FromUnixBytes(bytes));
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("A Windows pathspec record is not valid UTF-8.", exception);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + count > MaximumInputBytes)
            {
                throw new InvalidDataException(
                    $"Pathspec input exceeds the {MaximumInputBytes} byte limit.");
            }

            buffer.Write(chunk, 0, count);
        }
    }
}
