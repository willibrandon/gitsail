using System.Collections.Immutable;

namespace GitSail.Analyzers;

/// <summary>
/// Defines the complete locale coverage required by the GitSail release contract.
/// </summary>
internal static class RequiredLocaleSet
{
    /// <summary>
    /// Gets every required source locale in deterministic catalog order.
    /// </summary>
    internal static ImmutableArray<string> Names { get; } =
    [
        "bg",
        "de",
        "el",
        "en",
        "fr",
        "hu",
        "it",
        "ja",
        "nb",
        "pt-BR",
        "pt-PT",
        "ru",
        "sv",
        "vi",
        "zh-CN",
    ];

    /// <summary>
    /// Finds required locales that are absent from a supplied catalog set.
    /// </summary>
    /// <param name="locales">The normalized locale names supplied to the generator.</param>
    /// <returns>The missing required locales in deterministic order.</returns>
    internal static ImmutableArray<string> FindMissing(IEnumerable<string> locales)
    {
        var supplied = new HashSet<string>(locales, StringComparer.Ordinal);
        return [.. Names.Where(locale => !supplied.Contains(locale))];
    }
}
