using GitSail.Domain;

namespace GitSail.Ui;

/// <summary>
/// Resolves configured Unicode and ambiguous-width behavior into one output policy.
/// Uses conservative terminal and locale fallbacks when no explicit override exists.
/// </summary>
internal static class TerminalTextPolicyResolver
{
    /// <summary>
    /// Resolves one complete terminal text policy from typed configuration and environment hints.
    /// Treats a dumb terminal as ASCII and East Asian locales as width-two by default.
    /// </summary>
    /// <param name="configuration">The current typed configuration snapshot, when repository configuration is available.</param>
    /// <param name="term">The terminal type environment value.</param>
    /// <param name="locale">The effective process locale or culture name.</param>
    /// <returns>The conservative output-only terminal text policy.</returns>
    internal static TerminalTextPolicy Resolve(
        GitConfigurationSnapshot? configuration,
        string? term,
        string? locale)
    {
        var unicodeResolution = configuration?.Resolve(
            "gitsail.unicode",
            GitConfigurationScope.Local);
        var unicode = unicodeResolution?.EffectiveParsedValue?.Text;
        var useAscii = unicodeResolution?.EffectiveValidationError is not null || unicode switch
        {
            "ascii" => true,
            "unicode" => false,
            "auto" or null => string.Equals(term, "dumb", StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
        var widthResolution = configuration?.Resolve(
            "gitsail.ambiguouswidth",
            GitConfigurationScope.Local);
        var configuredWidth = widthResolution?.EffectiveParsedValue?.IntegerValue;
        var ambiguousWidth = configuredWidth switch
        {
            1 => 1,
            2 => 2,
            _ => UsesWideAmbiguousCharacters(locale) ? 2 : 1,
        };
        return new TerminalTextPolicy(useAscii, ambiguousWidth);
    }

    private static bool UsesWideAmbiguousCharacters(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
        {
            return false;
        }

        var normalized = locale.Trim().ToLowerInvariant();
        return normalized.StartsWith("ja", StringComparison.Ordinal) ||
            normalized.StartsWith("jp", StringComparison.Ordinal) ||
            normalized.StartsWith("japanese", StringComparison.Ordinal) ||
            normalized.StartsWith("ko", StringComparison.Ordinal) ||
            normalized.StartsWith("kr", StringComparison.Ordinal) ||
            normalized.StartsWith("korean", StringComparison.Ordinal) ||
            normalized.StartsWith("zh", StringComparison.Ordinal) ||
            normalized.StartsWith("chinese", StringComparison.Ordinal);
    }
}
