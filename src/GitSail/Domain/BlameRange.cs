using System.Globalization;

namespace GitSail.Domain;

/// <summary>
/// Represents one inclusive one-based line range requested from Git blame.
/// </summary>
/// <param name="Start">The inclusive first line number.</param>
/// <param name="End">The inclusive last line number.</param>
internal sealed record BlameRange(int Start, int End)
{
    /// <summary>
    /// Parses a range written as two positive decimal line numbers separated by a colon.
    /// </summary>
    /// <param name="value">The candidate range text.</param>
    /// <param name="range">The parsed range when successful.</param>
    /// <returns><see langword="true"/> when the range is valid.</returns>
    internal static bool TryParse(string value, out BlameRange? range)
    {
        range = null;
        ArgumentNullException.ThrowIfNull(value);
        var separator = value.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == value.Length - 1 ||
            value.IndexOf(':', separator + 1) >= 0 ||
            !int.TryParse(value.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out var start) ||
            !int.TryParse(value.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var end) ||
            start <= 0 || end < start)
        {
            return false;
        }

        range = new BlameRange(start, end);
        return true;
    }

    /// <inheritdoc />
    public override string ToString()
        => $"{Start.ToString(CultureInfo.InvariantCulture)},{End.ToString(CultureInfo.InvariantCulture)}";
}
