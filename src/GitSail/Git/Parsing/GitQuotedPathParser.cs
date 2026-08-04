using GitSail.Domain;
using System.Buffers;
using System.Text;

namespace GitSail.Git.Parsing;

/// <summary>
/// Parses Git's forced C-quoted patch path tokens back to exact platform path identity.
/// </summary>
internal static class GitQuotedPathParser
{
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// Parses the old and new paths from one complete <c>diff --git</c> header line.
    /// </summary>
    /// <param name="line">The exact header bytes without the line terminator.</param>
    /// <returns>The old and new exact repository paths without Git's side prefixes.</returns>
    internal static (GitPath OldPath, GitPath NewPath) ParseDiffHeader(ReadOnlySpan<byte> line)
    {
        ReadOnlySpan<byte> prefix = "diff --git "u8;
        if (!line.StartsWith(prefix))
        {
            throw new InvalidDataException("A raw diff file header had an invalid prefix.");
        }

        var remainder = line[prefix.Length..];
        var oldToken = ParseToken(ref remainder);
        if (remainder.IsEmpty || remainder[0] != (byte)' ')
        {
            throw new InvalidDataException("A raw diff file header did not separate its paths.");
        }

        remainder = remainder[1..];
        var newToken = ParseToken(ref remainder);
        if (!remainder.IsEmpty)
        {
            throw new InvalidDataException("A raw diff file header contained trailing path data.");
        }

        return (ParseRawPath(RemovePrefix(oldToken, "a/"u8)), ParseRawPath(RemovePrefix(newToken, "b/"u8)));
    }

    /// <summary>
    /// Creates an exact platform path from one nonempty NUL-delimited Git path field.
    /// </summary>
    /// <param name="bytes">The exact path bytes without a NUL terminator.</param>
    /// <returns>The exact native path identity.</returns>
    internal static GitPath ParseRawPath(ReadOnlySpan<byte> bytes)
        => OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath(s_strictUtf8.GetString(bytes))
            : GitPath.FromUnixBytes(bytes);

    private static byte[] ParseToken(ref ReadOnlySpan<byte> input)
    {
        if (input.IsEmpty)
        {
            throw new InvalidDataException("A raw diff file header contained an empty path token.");
        }

        if (input[0] != (byte)'"')
        {
            var separator = input.IndexOf((byte)' ');
            var token = separator < 0 ? input : input[..separator];
            input = separator < 0 ? [] : input[separator..];
            return token.ToArray();
        }

        input = input[1..];
        var writer = new ArrayBufferWriter<byte>(Math.Min(input.Length, 256));
        while (!input.IsEmpty)
        {
            var value = input[0];
            input = input[1..];
            if (value == (byte)'"')
            {
                return writer.WrittenSpan.ToArray();
            }

            if (value != (byte)'\\')
            {
                writer.Write([value]);
                continue;
            }

            if (input.IsEmpty)
            {
                throw new InvalidDataException("A raw diff path ended inside an escape sequence.");
            }

            var escaped = input[0];
            input = input[1..];
            if (escaped is >= (byte)'0' and <= (byte)'7')
            {
                var decoded = escaped - (byte)'0';
                var digits = 1;
                while (digits < 3 && !input.IsEmpty && input[0] is >= (byte)'0' and <= (byte)'7')
                {
                    decoded = (decoded * 8) + input[0] - (byte)'0';
                    input = input[1..];
                    digits++;
                }

                if (decoded > byte.MaxValue)
                {
                    throw new InvalidDataException("A raw diff path contained an out-of-range octal escape.");
                }

                writer.Write([(byte)decoded]);
                continue;
            }

            writer.Write([escaped switch
            {
                (byte)'a' => (byte)'\a',
                (byte)'b' => (byte)'\b',
                (byte)'t' => (byte)'\t',
                (byte)'n' => (byte)'\n',
                (byte)'v' => (byte)'\v',
                (byte)'f' => (byte)'\f',
                (byte)'r' => (byte)'\r',
                (byte)'\\' => (byte)'\\',
                (byte)'"' => (byte)'"',
                _ => throw new InvalidDataException("A raw diff path contained an unknown escape sequence."),
            }]);
        }

        throw new InvalidDataException("A raw diff path had no closing quote.");
    }

    private static ReadOnlySpan<byte> RemovePrefix(byte[] token, ReadOnlySpan<byte> prefix)
    {
        if (!token.AsSpan().StartsWith(prefix) || token.Length == prefix.Length)
        {
            throw new InvalidDataException("A raw diff path did not contain the required side prefix.");
        }

        return token.AsSpan(prefix.Length);
    }

}
