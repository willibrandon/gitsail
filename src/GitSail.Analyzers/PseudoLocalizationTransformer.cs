using System.Globalization;
using System.Text;

namespace GitSail.Analyzers;

/// <summary>
/// Derives expansion and right-to-left test patterns from validated English messages.
/// </summary>
internal static class PseudoLocalizationTransformer
{
    /// <summary>
    /// Produces one deterministic pseudo-localized message pattern.
    /// </summary>
    /// <param name="pattern">The validated English pattern.</param>
    /// <param name="rightToLeft">Whether to reverse presentation order inside RTL isolation.</param>
    /// <returns>The pseudo-localized pattern with named argument markers preserved.</returns>
    internal static string Transform(string pattern, bool rightToLeft)
    {
        _ = LocalizationPatternParser.TryParse(pattern, out var parts, out _);
        var builder = new StringBuilder(pattern.Length * 2);
        if (rightToLeft)
        {
            builder.Append('\u2067').Append('⟦');
            for (var index = parts.Length - 1; index >= 0; index--)
            {
                AppendPart(builder, parts[index], reverse: true, expand: false);
            }

            return builder.Append('⟧').Append('\u2069').ToString();
        }

        builder.Append('⟦');
        foreach (var part in parts)
        {
            AppendPart(builder, part, reverse: false, expand: true);
        }

        return builder.Append("~~⟧").ToString();
    }

    private static void AppendPart(
        StringBuilder builder,
        LocalizationPatternPart part,
        bool reverse,
        bool expand)
    {
        if (part.IsArgument)
        {
            builder.Append("{ $").Append(part.Text).Append(" }");
            return;
        }

        var transformed = Accent(part.Text, expand);
        if (!reverse)
        {
            builder.Append(transformed);
            return;
        }

        var textElements = StringInfo.ParseCombiningCharacters(transformed);
        for (var index = textElements.Length - 1; index >= 0; index--)
        {
            var start = textElements[index];
            var length = index + 1 < textElements.Length
                ? textElements[index + 1] - start
                : transformed.Length - start;
            builder.Append(transformed, start, length);
        }
    }

    private static string Accent(string value, bool expand)
    {
        var builder = new StringBuilder(value.Length * 2);
        foreach (var character in value)
        {
            var accented = Accent(character);
            builder.Append(accented);
            if (expand && IsAsciiVowel(character))
            {
                builder.Append(accented);
            }
        }

        return builder.ToString();
    }

    private static char Accent(char value)
        => value switch
        {
            'A' => 'Å',
            'B' => 'Ɓ',
            'C' => 'Ç',
            'D' => 'Ď',
            'E' => 'É',
            'F' => 'Ƒ',
            'G' => 'Ĝ',
            'H' => 'Ĥ',
            'I' => 'Ï',
            'J' => 'Ĵ',
            'K' => 'Ķ',
            'L' => 'Ļ',
            'M' => 'Ṁ',
            'N' => 'Ñ',
            'O' => 'Ö',
            'P' => 'Þ',
            'Q' => 'Ǫ',
            'R' => 'Ŕ',
            'S' => 'Š',
            'T' => 'Ţ',
            'U' => 'Ü',
            'V' => 'Ṽ',
            'W' => 'Ŵ',
            'X' => 'Ẍ',
            'Y' => 'Ÿ',
            'Z' => 'Ž',
            'a' => 'å',
            'b' => 'ƀ',
            'c' => 'ç',
            'd' => 'ď',
            'e' => 'é',
            'f' => 'ƒ',
            'g' => 'ĝ',
            'h' => 'ĥ',
            'i' => 'ï',
            'j' => 'ĵ',
            'k' => 'ķ',
            'l' => 'ļ',
            'm' => 'ṁ',
            'n' => 'ñ',
            'o' => 'ö',
            'p' => 'þ',
            'q' => 'ǫ',
            'r' => 'ŕ',
            's' => 'š',
            't' => 'ţ',
            'u' => 'ü',
            'v' => 'ṽ',
            'w' => 'ŵ',
            'x' => 'ẍ',
            'y' => 'ÿ',
            'z' => 'ž',
            _ => value,
        };

    private static bool IsAsciiVowel(char value)
        => value is 'A' or 'E' or 'I' or 'O' or 'U' or 'a' or 'e' or 'i' or 'o' or 'u';
}
