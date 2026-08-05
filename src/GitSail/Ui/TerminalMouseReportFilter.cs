namespace GitSail.Ui;

/// <summary>
/// Removes a fragmented SGR mouse report if a console host exposes it as filter text.
/// </summary>
internal sealed class TerminalMouseReportFilter
{
    private bool _discardingReport;

    /// <summary>
    /// Returns user text without any complete or currently fragmented SGR mouse report.
    /// </summary>
    /// <param name="text">The latest complete text-box value or input fragment.</param>
    /// <returns>The text that may safely drive a user-visible filter.</returns>
    internal string Filter(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_discardingReport)
        {
            var terminator = text.IndexOfAny(['M', 'm']);
            if (terminator < 0)
            {
                return string.Empty;
            }

            _discardingReport = false;
            return Filter(text[(terminator + 1)..]);
        }

        var marker = text.IndexOf("[<", StringComparison.Ordinal);
        if (marker < 0)
        {
            return text;
        }

        var index = marker + 2;
        var semicolonCount = 0;
        var digitCount = 0;
        while (index < text.Length)
        {
            var character = text[index];
            if (char.IsAsciiDigit(character))
            {
                digitCount++;
                index++;
                continue;
            }

            if (character == ';')
            {
                semicolonCount++;
                index++;
                continue;
            }

            if ((character == 'M' || character == 'm') &&
                semicolonCount == 2 &&
                digitCount >= 3)
            {
                return text[..marker] + Filter(text[(index + 1)..]);
            }

            return text;
        }

        if (semicolonCount > 0 && digitCount > 0)
        {
            _discardingReport = true;
            return text[..marker];
        }

        return text;
    }
}
