using System.Buffers;
using System.Globalization;
using System.Text;

namespace GitSail.Ui;

/// <summary>
/// Converts raw patch bytes into terminal-safe UTF-8 presentation text without changing source bytes.
/// </summary>
internal static class RawPatchPresentationDecoder
{
    /// <summary>
    /// Decodes one exact patch prefix while preserving lines and exposing invalid or unsafe bytes.
    /// </summary>
    /// <param name="bytes">The exact patch bytes selected for presentation.</param>
    /// <param name="isTruncated">Whether additional patch bytes remain in the raw spool.</param>
    /// <returns>Terminal-safe editor text that is never suitable as mutation input.</returns>
    internal static string Decode(ReadOnlySpan<byte> bytes, bool isTruncated)
    {
        var builder = new StringBuilder(bytes.Length);
        while (!bytes.IsEmpty)
        {
            if (bytes[0] == (byte)'\n')
            {
                builder.Append('\n');
                bytes = bytes[1..];
                continue;
            }

            if (bytes[0] == (byte)'\r' && bytes.Length > 1 && bytes[1] == (byte)'\n')
            {
                builder.Append('\n');
                bytes = bytes[2..];
                continue;
            }

            var status = Rune.DecodeFromUtf8(bytes, out var rune, out var consumed);
            if (status != OperationStatus.Done)
            {
                AppendByteToken(builder, bytes[0]);
                bytes = bytes[1..];
                continue;
            }

            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or
                UnicodeCategory.Format or
                UnicodeCategory.LineSeparator or
                UnicodeCategory.ParagraphSeparator)
            {
                AppendUnicodeToken(builder, rune);
            }
            else
            {
                builder.Append(rune);
            }

            bytes = bytes[consumed..];
        }

        if (isTruncated)
        {
            if (builder.Length > 0 && builder[^1] != '\n')
            {
                builder.Append('\n');
            }

            builder.Append("<patch presentation truncated; exact bytes remain available>");
        }

        return builder.ToString();
    }

    private static void AppendByteToken(StringBuilder builder, byte value)
        => builder.Append("<0x")
            .Append(value.ToString("X2", CultureInfo.InvariantCulture))
            .Append('>');

    private static void AppendUnicodeToken(StringBuilder builder, Rune rune)
        => builder.Append("<U+")
            .Append(rune.Value.ToString("X4", CultureInfo.InvariantCulture))
            .Append('>');
}
