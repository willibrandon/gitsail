using GitSail.Domain;
using System.Buffers;
using System.Text;

namespace GitSail.Git.Parsing;

/// <summary>
/// Parses one raw or C-quoted path field from Git blame's line-framed protocol.
/// </summary>
internal static class BlamePathParser
{
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// Parses one complete nonempty path field without a line terminator.
    /// </summary>
    /// <param name="field">The raw or C-quoted path field.</param>
    /// <returns>The exact native path represented by the field.</returns>
    internal static GitPath Parse(ReadOnlySpan<byte> field)
    {
        if (field.IsEmpty)
        {
            throw new InvalidDataException("Git blame returned an empty path.");
        }

        var bytes = field[0] == (byte)'"' ? ParseQuoted(field) : field.ToArray();
        if (bytes.Length == 0)
        {
            throw new InvalidDataException("Git blame returned an empty path.");
        }

        return OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath(s_strictUtf8.GetString(bytes))
            : GitPath.FromUnixBytes(bytes);
    }

    private static byte[] ParseQuoted(ReadOnlySpan<byte> field)
    {
        if (field.Length < 2 || field[^1] != (byte)'"')
        {
            throw new InvalidDataException("Git blame returned an unterminated quoted path.");
        }

        var input = field[1..^1];
        var writer = new ArrayBufferWriter<byte>(Math.Min(input.Length, 256));
        while (!input.IsEmpty)
        {
            var value = input[0];
            input = input[1..];
            if (value != (byte)'\\')
            {
                writer.Write([value]);
                continue;
            }

            if (input.IsEmpty)
            {
                throw new InvalidDataException("Git blame returned a path ending inside an escape sequence.");
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
                    throw new InvalidDataException("Git blame returned an out-of-range path escape.");
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
                _ => throw new InvalidDataException("Git blame returned an unknown path escape."),
            }]);
        }

        return writer.WrittenSpan.ToArray();
    }
}
