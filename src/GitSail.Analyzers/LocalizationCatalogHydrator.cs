using System.Collections.Immutable;

namespace GitSail.Analyzers;

/// <summary>
/// Applies English message contracts to concise non-English translation entries.
/// </summary>
internal static class LocalizationCatalogHydrator
{
    /// <summary>
    /// Inherits non-translatable metadata from the single available English catalog.
    /// </summary>
    /// <param name="catalogs">The parsed catalogs to hydrate in place.</param>
    internal static void Hydrate(ImmutableArray<LocalizationCatalog> catalogs)
    {
        var englishCatalogs = catalogs
            .Where(static catalog => string.Equals(
                catalog.Document.Locale,
                "en",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (englishCatalogs.Length != 1 || englishCatalogs[0].Document.Messages is null)
        {
            return;
        }

        var englishMessages = englishCatalogs[0].Document.Messages
            .Where(static message => message.Id is not null)
            .ToDictionary(static message => message.Id!, StringComparer.Ordinal);
        foreach (var catalog in catalogs)
        {
            if (ReferenceEquals(catalog, englishCatalogs[0]) || catalog.Document.Messages is null)
            {
                continue;
            }

            foreach (var translation in catalog.Document.Messages)
            {
                if (translation.Id is null || !englishMessages.TryGetValue(translation.Id, out var english))
                {
                    continue;
                }

                InheritContract(translation, english);
            }
        }
    }

    private static void InheritContract(
        LocalizationMessageDocument translation,
        LocalizationMessageDocument english)
    {
        translation.Description ??= english.Description;
        translation.Arguments ??= english.Arguments is null
            ? null
            : new Dictionary<string, string>(english.Arguments, StringComparer.Ordinal);
        translation.Selector ??= english.Selector is null
            ? null
            : new LocalizationSelectorDocument
            {
                Kind = english.Selector.Kind,
                Argument = english.Selector.Argument,
            };
        translation.Menu ??= english.Menu;
        var inheritsWidthPolicy = translation.WidthPolicy is null;
        translation.WidthPolicy ??= english.WidthPolicy;
        if (inheritsWidthPolicy)
        {
            translation.MaximumColumns ??= english.MaximumColumns;
        }
    }
}
