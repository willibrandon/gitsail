using Hex1b;
using System.Globalization;
using System.Text;

namespace GitSail.Ui;

/// <summary>
/// Transforms final UTF-8 terminal bytes without changing retained application text.
/// Preserves display width while providing complete ASCII and width-two fallbacks.
/// </summary>
internal sealed class TerminalTextOutputTransformer
{
    private static readonly UTF8Encoding s_utf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: false);
    private readonly Decoder _decoder = s_utf8.GetDecoder();
    private bool _decoderActive;

    /// <summary>
    /// Applies the current output policy to one ordered terminal byte block.
    /// Retains incomplete trailing UTF-8 bytes until the next ordered block arrives.
    /// </summary>
    /// <param name="data">The ordered terminal output bytes to present.</param>
    /// <param name="policy">The current output-only terminal text policy.</param>
    /// <returns>UTF-8 bytes whose visible cell widths match the conservative rendering policy.</returns>
    internal byte[] Transform(ReadOnlySpan<byte> data, TerminalTextPolicy policy)
    {
        if (!policy.RequiresTransformation && !_decoderActive)
        {
            return data.ToArray();
        }

        _decoderActive = true;
        var characters = new char[s_utf8.GetMaxCharCount(data.Length)];
        _decoder.Convert(
            data,
            characters,
            flush: false,
            out var bytesUsed,
            out var charactersUsed,
            out _);
        if (bytesUsed != data.Length)
        {
            throw new InvalidDataException("Terminal output exceeded the bounded UTF-8 decoder buffer.");
        }

        var text = new string(characters, 0, charactersUsed);
        if (text.Length == 0)
        {
            return [];
        }

        var transformed = new StringBuilder(text.Length);
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (policy.UseAscii)
            {
                AppendAscii(transformed, element);
            }
            else if (policy.AmbiguousWidth == 2 && ShouldReplaceAmbiguous(element))
            {
                transformed.Append('?', DisplayWidth.GetGraphemeWidth(element));
            }
            else
            {
                transformed.Append(element);
            }
        }

        return s_utf8.GetBytes(transformed.ToString());
    }

    private static bool ShouldReplaceAmbiguous(string element)
    {
        foreach (var rune in element.EnumerateRunes())
        {
            if (DisplayWidth.GetRuneWidth(rune) > 0 && TerminalEastAsianWidth.IsAmbiguous(rune.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static void AppendAscii(StringBuilder output, string element)
    {
        var width = DisplayWidth.GetGraphemeWidth(element);
        if (width == 0)
        {
            return;
        }

        var runeEnumerator = element.EnumerateRunes();
        if (!runeEnumerator.MoveNext())
        {
            return;
        }

        var first = runeEnumerator.Current;
        if (first.IsAscii && first.Value >= 0x20 && first.Value <= 0x7E)
        {
            output.Append((char)first.Value);
            if (width > 1)
            {
                output.Append('?', width - 1);
            }

            return;
        }

        var replacement = GetAsciiReplacement(first.Value);
        output.Append(replacement);
        if (width > 1)
        {
            output.Append(replacement == ' ' ? ' ' : '?', width - 1);
        }
    }

    private static char GetAsciiReplacement(int value)
    {
        if (value is >= 0x2500 and <= 0x257F)
        {
            return value switch
            {
                0x2500 or 0x2501 or 0x2504 or 0x2505 or 0x2508 or 0x2509 or
                    0x254C or 0x254D or 0x2550 or 0x2574 or 0x2576 => '-',
                0x2502 or 0x2503 or 0x2506 or 0x2507 or 0x250A or 0x250B or
                    0x254E or 0x254F or 0x2551 or 0x2575 or 0x2577 => '|',
                _ => '+',
            };
        }

        if (value is >= 0x2580 and <= 0x259F)
        {
            return '#';
        }

        return value switch
        {
            0x2022 or 0x25CF => '*',
            0x2026 => '.',
            0x2190 => '<',
            0x2191 => '^',
            0x2192 => '>',
            0x2193 => 'v',
            0x2194 => '=',
            0x2713 => 'v',
            0x2717 => 'x',
            _ => '?',
        };
    }
}
