using System.Globalization;
using System.Text;

namespace GitSail.Ui;

/// <summary>
/// Converts untrusted text controls and formatting characters into visible terminal-safe tokens.
/// </summary>
internal static class TerminalTextSanitizer
{
    /// <summary>
    /// Sanitizes one untrusted string without altering printable text.
    /// </summary>
    /// <param name="text">The untrusted text to sanitize.</param>
    /// <returns>The terminal-safe display text.</returns>
    internal static string Sanitize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or
                UnicodeCategory.Format or
                UnicodeCategory.LineSeparator or
                UnicodeCategory.ParagraphSeparator)
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

        return builder.ToString();
    }
}
