using System.Collections.Immutable;
using System.Text;

namespace GitSail.Ui;

/// <summary>
/// Decodes exact file bytes into terminal-safe display lines using BOM and Git encoding settings.
/// </summary>
internal static class FileContentPresentationDecoder
{
    private static readonly Encoding s_bigEndianUtf32 = new UTF32Encoding(
        bigEndian: true,
        byteOrderMark: true,
        throwOnInvalidCharacters: true);

    static FileContentPresentationDecoder()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// Decodes complete file bytes without changing the retained source content.
    /// </summary>
    /// <param name="bytes">The complete exact file bytes.</param>
    /// <param name="configuredEncodingName">The effective Git GUI encoding name.</param>
    /// <returns>The display lines, encoding label, and optional fallback warning.</returns>
    internal static (ImmutableArray<string> Lines, string EncodingName, string? Warning) DecodeLines(
        ReadOnlySpan<byte> bytes,
        string configuredEncodingName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredEncodingName);
        var encodingName = configuredEncodingName;
        var payload = bytes;
        Encoding encoding;
        if (payload.StartsWith(Encoding.UTF32.Preamble))
        {
            encoding = CreateStrictEncoding(Encoding.UTF32);
            encodingName = "UTF-32 LE";
            payload = payload[Encoding.UTF32.Preamble.Length..];
        }
        else if (payload.StartsWith(s_bigEndianUtf32.Preamble))
        {
            encoding = s_bigEndianUtf32;
            encodingName = "UTF-32 BE";
            payload = payload[s_bigEndianUtf32.Preamble.Length..];
        }
        else if (payload.StartsWith(Encoding.UTF8.Preamble))
        {
            encoding = CreateStrictEncoding(Encoding.UTF8);
            encodingName = "UTF-8 with BOM";
            payload = payload[Encoding.UTF8.Preamble.Length..];
        }
        else if (payload.StartsWith(Encoding.Unicode.Preamble))
        {
            encoding = CreateStrictEncoding(Encoding.Unicode);
            encodingName = "UTF-16 LE";
            payload = payload[Encoding.Unicode.Preamble.Length..];
        }
        else if (payload.StartsWith(Encoding.BigEndianUnicode.Preamble))
        {
            encoding = CreateStrictEncoding(Encoding.BigEndianUnicode);
            encodingName = "UTF-16 BE";
            payload = payload[Encoding.BigEndianUnicode.Preamble.Length..];
        }
        else
        {
            try
            {
                encoding = Encoding.GetEncoding(
                    configuredEncodingName,
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback);
            }
            catch (ArgumentException)
            {
                return DecodeRaw(bytes, configuredEncodingName, $"Unknown encoding '{configuredEncodingName}'; showing exact byte tokens.");
            }
        }

        try
        {
            var text = encoding.GetString(payload);
            return (SplitAndSanitize(text), encodingName, null);
        }
        catch (DecoderFallbackException)
        {
            return DecodeRaw(bytes, configuredEncodingName, $"Content is not valid {configuredEncodingName}; showing exact byte tokens.");
        }
    }

    private static (ImmutableArray<string> Lines, string EncodingName, string? Warning) DecodeRaw(
        ReadOnlySpan<byte> bytes,
        string encodingName,
        string warning)
        => (SplitPresentation(RawPatchPresentationDecoder.Decode(bytes, isTruncated: false)), encodingName, warning);

    private static ImmutableArray<string> SplitAndSanitize(string text)
        => [.. SplitPresentation(text).Select(TerminalTextSanitizer.Sanitize)];

    private static ImmutableArray<string> SplitPresentation(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var lines = text.Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].EndsWith('\r'))
            {
                lines[index] = lines[index][..^1];
            }
        }

        return [.. lines];
    }

    private static Encoding CreateStrictEncoding(Encoding encoding)
        => Encoding.GetEncoding(
            encoding.CodePage,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
}
