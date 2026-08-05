namespace GitSail.Localization;

/// <summary>
/// Normalizes BCP 47 and common POSIX locale names for generated catalog lookup.
/// </summary>
internal static class LocaleNameNormalizer
{
    /// <summary>
    /// Normalizes a locale name without probing files or loading resources dynamically.
    /// </summary>
    /// <param name="localeName">The BCP 47 or POSIX locale name.</param>
    /// <returns>The normalized language or language-region name, or <c>en</c> when no usable name exists.</returns>
    internal static string Normalize(string? localeName)
    {
        if (string.IsNullOrWhiteSpace(localeName) ||
            string.Equals(localeName, "C", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(localeName, "POSIX", StringComparison.OrdinalIgnoreCase))
        {
            return "en";
        }

        var end = localeName.Length;
        var encodingSeparator = localeName.IndexOf('.');
        if (encodingSeparator >= 0)
        {
            end = Math.Min(end, encodingSeparator);
        }

        var modifierSeparator = localeName.IndexOf('@');
        if (modifierSeparator >= 0)
        {
            end = Math.Min(end, modifierSeparator);
        }

        var core = localeName[..end].Replace('_', '-');
        var parts = core.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return "en";
        }

        var language = parts[0].ToLowerInvariant();
        if (parts.Length == 1)
        {
            return language;
        }

        var region = parts[1].Length == 2
            ? parts[1].ToUpperInvariant()
            : parts[1];
        return $"{language}-{region}";
    }
}
