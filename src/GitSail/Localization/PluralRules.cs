namespace GitSail.Localization;

/// <summary>
/// Applies cardinal plural rules for every locale required by GitSail.
/// </summary>
internal static class PluralRules
{
    /// <summary>
    /// Selects the cardinal category for a non-negative integer count.
    /// </summary>
    /// <param name="locale">The normalized locale selected by the generated catalog.</param>
    /// <param name="count">The count used by the plural message.</param>
    /// <returns>The applicable plural category.</returns>
    internal static PluralCategory GetCategory(string locale, long count)
    {
        if (count < 0)
        {
            return PluralCategory.Other;
        }

        if (locale == "ru")
        {
            var modulo10 = count % 10;
            var modulo100 = count % 100;
            if (modulo10 == 1 && modulo100 != 11)
            {
                return PluralCategory.One;
            }

            if (modulo10 is >= 2 and <= 4 && modulo100 is < 12 or > 14)
            {
                return PluralCategory.Few;
            }

            if (modulo10 == 0 || modulo10 is >= 5 and <= 9 || modulo100 is >= 11 and <= 14)
            {
                return PluralCategory.Many;
            }

            return PluralCategory.Other;
        }

        if (locale is "fr" or "pt-BR")
        {
            if (count is 0 or 1)
            {
                return PluralCategory.One;
            }

            return IsNonZeroWholeMillion(count)
                ? PluralCategory.Many
                : PluralCategory.Other;
        }

        if (locale == "pt-PT")
        {
            if (count == 1)
            {
                return PluralCategory.One;
            }

            return IsNonZeroWholeMillion(count)
                ? PluralCategory.Many
                : PluralCategory.Other;
        }

        if (locale is "ja" or "vi" or "zh-CN")
        {
            return PluralCategory.Other;
        }

        return count == 1 ? PluralCategory.One : PluralCategory.Other;
    }

    private static bool IsNonZeroWholeMillion(long count)
        => count != 0 && count % 1_000_000 == 0;
}
