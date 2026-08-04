using System.Globalization;
using System.Text;

namespace GitSail.Ui;

/// <summary>
/// Preserves lines while making bounded child-process text safe for terminal editors.
/// </summary>
internal static class TerminalOutputFormatter
{
    /// <summary>
    /// Decodes one bounded UTF-8 channel and sanitizes terminal controls.
    /// </summary>
    /// <param name="bytes">The exact bounded child-output bytes.</param>
    /// <returns>Line-preserving terminal-safe text.</returns>
    internal static string Format(ReadOnlySpan<byte> bytes)
        => Format(Encoding.UTF8.GetString(bytes));

    /// <summary>
    /// Sanitizes decoded child-process text while retaining line and tab structure.
    /// </summary>
    /// <param name="text">The decoded untrusted process text.</param>
    /// <returns>Line-preserving terminal-safe text.</returns>
    internal static string Format(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        var pendingCarriageReturn = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (pendingCarriageReturn)
            {
                if (rune.Value == '\n')
                {
                    builder.Append('\n');
                    pendingCarriageReturn = false;
                    continue;
                }

                builder.Append('\n');
                pendingCarriageReturn = false;
            }

            if (rune.Value == '\r')
            {
                pendingCarriageReturn = true;
            }
            else if (rune.Value is '\n' or '\t')
            {
                builder.Append((char)rune.Value);
            }
            else if (IsUnsafe(rune))
            {
                builder.Append("<U+")
                    .Append(rune.Value.ToString("X4", CultureInfo.InvariantCulture))
                    .Append('>');
            }
            else
            {
                builder.Append(rune);
            }
        }

        if (pendingCarriageReturn)
        {
            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static bool IsUnsafe(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.Control or
            UnicodeCategory.Format or
            UnicodeCategory.LineSeparator or
            UnicodeCategory.ParagraphSeparator;
    }
}
