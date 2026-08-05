using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Globalization;

namespace GitSail.Analyzers;

/// <summary>
/// Enforces catalog safety, translation compatibility, and presentation metadata.
/// </summary>
internal static class LocalizationCatalogValidator
{
    private static readonly ImmutableHashSet<string> s_argumentKinds =
        ImmutableHashSet.Create(StringComparer.Ordinal, "string", "integer", "number");
    private static readonly ImmutableHashSet<string> s_widthPolicies =
        ImmutableHashSet.Create(StringComparer.Ordinal, "wrap", "clip", "hard");
    private static readonly ImmutableHashSet<string> s_pluralCategories =
        ImmutableHashSet.Create(StringComparer.Ordinal, "zero", "one", "two", "few", "many", "other");
    private static readonly ImmutableHashSet<string> s_oneOtherPluralCategories =
        ImmutableHashSet.Create(StringComparer.Ordinal, "one", "other");
    private static readonly ImmutableHashSet<string> s_oneManyOtherPluralCategories =
        ImmutableHashSet.Create(StringComparer.Ordinal, "one", "many", "other");
    private static readonly ImmutableHashSet<string> s_russianPluralCategories =
        ImmutableHashSet.Create(StringComparer.Ordinal, "one", "few", "many", "other");
    private static readonly ImmutableHashSet<string> s_otherPluralCategory =
        ImmutableHashSet.Create(StringComparer.Ordinal, "other");

    /// <summary>
    /// Validates every parsed catalog and reports precise build errors.
    /// </summary>
    /// <param name="catalogs">The parsed catalogs.</param>
    /// <param name="context">The generator output context.</param>
    /// <param name="requireCompleteLocales">Whether every release locale must be present.</param>
    /// <returns><see langword="true"/> when source generation may continue.</returns>
    internal static bool Validate(
        ImmutableArray<LocalizationCatalog> catalogs,
        SourceProductionContext context,
        bool requireCompleteLocales)
    {
        if (catalogs.IsDefaultOrEmpty)
        {
            ReportInvalid(context, "locales", "no localization catalogs were supplied");
            return false;
        }

        var valid = true;
        var locales = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var catalog in catalogs)
        {
            valid &= ValidateCatalog(catalog, locales, context);
        }

        var englishCatalogs = catalogs
            .Where(static catalog => string.Equals(catalog.Document.Locale, "en", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (englishCatalogs.Length != 1)
        {
            ReportInvalid(context, "locales", "exactly one English catalog with locale 'en' is required");
            return false;
        }

        if (!valid)
        {
            return false;
        }

        if (requireCompleteLocales)
        {
            var missingLocales = RequiredLocaleSet.FindMissing(
                catalogs.Select(static catalog => catalog.Document.Locale!));
            if (!missingLocales.IsDefaultOrEmpty)
            {
                ReportInvalid(
                    context,
                    "locales",
                    $"required locale catalogs are missing: {string.Join(", ", missingLocales)}");
                return false;
            }
        }

        var english = englishCatalogs[0];
        var englishMessages = english.Document.Messages!
            .ToDictionary(static message => message.Id!, StringComparer.Ordinal);
        foreach (var catalog in catalogs)
        {
            if (!ReferenceEquals(catalog, english))
            {
                valid &= ValidateCompatibility(catalog, englishMessages, context);
            }
        }

        var generatedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in englishMessages.Values)
        {
            var generatedName = LocalizationSourceEmitter.GetMemberName(message.Id!);
            if (!generatedNames.Add(generatedName))
            {
                ReportInvalid(
                    context,
                    english.Path,
                    $"message id '{message.Id}' collides with another generated member named '{generatedName}'");
                valid = false;
            }
        }

        return valid;
    }

    private static bool ValidateCatalog(
        LocalizationCatalog catalog,
        HashSet<string> locales,
        SourceProductionContext context)
    {
        var document = catalog.Document;
        var valid = true;
        if (!IsValidLocale(document.Locale))
        {
            ReportInvalid(context, catalog.Path, "locale must be a normalized BCP 47 language or language-region name");
            valid = false;
        }
        else
        {
            var fileLocale = Path.GetFileNameWithoutExtension(catalog.Path);
            if (!string.Equals(fileLocale, document.Locale, StringComparison.OrdinalIgnoreCase))
            {
                ReportInvalid(context, catalog.Path, $"file name '{fileLocale}' must match locale '{document.Locale}'");
                valid = false;
            }

            if (!locales.Add(document.Locale!))
            {
                ReportInvalid(context, catalog.Path, $"locale '{document.Locale}' is declared more than once");
                valid = false;
            }
        }

        if (!string.Equals(document.License, "MIT", StringComparison.Ordinal))
        {
            ReportInvalid(context, catalog.Path, "license must be exactly 'MIT'");
            valid = false;
        }

        if (!document.Reviewed)
        {
            ReportInvalid(context, catalog.Path, "catalog review must be complete before compilation");
            valid = false;
        }

        if (document.Messages is not { Length: > 0 })
        {
            ReportInvalid(context, catalog.Path, "messages must contain at least one entry");
            return false;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var accelerators = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var message in document.Messages)
        {
            valid &= ValidateMessage(catalog, message, ids, accelerators, context);
        }

        return valid;
    }

    private static bool ValidateMessage(
        LocalizationCatalog catalog,
        LocalizationMessageDocument message,
        HashSet<string> ids,
        HashSet<string> accelerators,
        SourceProductionContext context)
    {
        var valid = true;
        if (!IsValidMessageId(message.Id))
        {
            ReportInvalid(context, catalog.Path, "every message id must contain lowercase semantic segments separated by periods");
            valid = false;
        }
        else if (!ids.Add(message.Id!))
        {
            ReportInvalid(context, catalog.Path, $"message id '{message.Id}' is duplicated");
            valid = false;
        }

        if (string.IsNullOrWhiteSpace(message.Description))
        {
            ReportInvalid(context, catalog.Path, $"message '{message.Id}' requires a translator description");
            valid = false;
        }

        var arguments = message.Arguments ?? new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var argument in arguments)
        {
            if (!SyntaxFacts.IsValidIdentifier(argument.Key) ||
                SyntaxFacts.GetKeywordKind(argument.Key) != SyntaxKind.None)
            {
                ReportInvalid(context, catalog.Path, $"message '{message.Id}' has invalid argument name '{argument.Key}'");
                valid = false;
            }

            if (!s_argumentKinds.Contains(argument.Value))
            {
                ReportInvalid(context, catalog.Path, $"message '{message.Id}' has unsupported type '{argument.Value}' for '{argument.Key}'");
                valid = false;
            }
        }

        var patterns = new List<string>();
        if (message.Selector is null)
        {
            if (message.Text is null || message.Variants is not null)
            {
                ReportInvalid(context, catalog.Path, $"message '{message.Id}' must define text or a selector with variants, but not both");
                valid = false;
            }
            else
            {
                patterns.Add(message.Text);
            }
        }
        else
        {
            valid &= ValidateSelector(catalog, message, arguments, context);
            if (message.Text is not null || message.Variants is not { Count: > 0 })
            {
                ReportInvalid(context, catalog.Path, $"selected message '{message.Id}' must define variants and must not define text");
                valid = false;
            }
            else
            {
                patterns.AddRange(message.Variants.Values);
            }
        }

        var usedArguments = new HashSet<string>(StringComparer.Ordinal);
        if (message.Selector?.Argument is { } selectorArgument)
        {
            usedArguments.Add(selectorArgument);
        }

        foreach (var pattern in patterns)
        {
            valid &= ValidatePattern(catalog, message, pattern, arguments, usedArguments, context);
        }

        foreach (var argument in arguments.Keys)
        {
            if (!usedArguments.Contains(argument))
            {
                ReportInvalid(context, catalog.Path, $"message '{message.Id}' declares unused argument '{argument}'");
                valid = false;
            }
        }

        valid &= ValidatePresentation(catalog, message, patterns, accelerators, context);
        return valid;
    }

    private static bool ValidateSelector(
        LocalizationCatalog catalog,
        LocalizationMessageDocument message,
        Dictionary<string, string> arguments,
        SourceProductionContext context)
    {
        var selector = message.Selector!;
        if (selector.Kind is not "plural" and not "select")
        {
            ReportInvalid(context, catalog.Path, $"message '{message.Id}' selector kind must be 'plural' or 'select'");
            return false;
        }

        if (selector.Argument is null || !arguments.TryGetValue(selector.Argument, out var argumentKind))
        {
            ReportInvalid(context, catalog.Path, $"message '{message.Id}' selector must name a declared argument");
            return false;
        }

        var valid = true;
        if (selector.Kind == "plural" && argumentKind != "integer")
        {
            ReportInvalid(context, catalog.Path, $"message '{message.Id}' plural selector argument must have type 'integer'");
            valid = false;
        }

        if (selector.Kind == "select" && argumentKind != "string")
        {
            ReportInvalid(context, catalog.Path, $"message '{message.Id}' value selector argument must have type 'string'");
            valid = false;
        }

        if (message.Variants is { } variants)
        {
            if (!variants.ContainsKey("other"))
            {
                ReportInvalid(context, catalog.Path, $"message '{message.Id}' selector requires an 'other' variant");
                valid = false;
            }

            if (selector.Kind == "plural")
            {
                foreach (var category in variants.Keys)
                {
                    if (!s_pluralCategories.Contains(category))
                    {
                        ReportInvalid(context, catalog.Path, $"message '{message.Id}' has invalid plural category '{category}'");
                        valid = false;
                    }
                }

                valid &= ValidateLocalePluralCategories(catalog, message, variants, context);
            }
        }

        return valid;
    }

    private static bool ValidateLocalePluralCategories(
        LocalizationCatalog catalog,
        LocalizationMessageDocument message,
        Dictionary<string, string> variants,
        SourceProductionContext context)
    {
        var required = GetRequiredPluralCategories(catalog.Document.Locale!);
        if (required is null)
        {
            return true;
        }

        var valid = true;
        foreach (var category in required)
        {
            if (!variants.ContainsKey(category))
            {
                ReportInvalid(
                    context,
                    catalog.Path,
                    $"message '{message.Id}' requires plural category '{category}' for locale '{catalog.Document.Locale}'");
                valid = false;
            }
        }

        foreach (var category in variants.Keys)
        {
            if (!required.Contains(category))
            {
                ReportInvalid(
                    context,
                    catalog.Path,
                    $"message '{message.Id}' cannot use plural category '{category}' for locale '{catalog.Document.Locale}'");
                valid = false;
            }
        }

        return valid;
    }

    private static ImmutableHashSet<string>? GetRequiredPluralCategories(string locale)
        => locale switch
        {
            "bg" or "de" or "el" or "en" or "hu" or "it" or "nb" or "sv" =>
                s_oneOtherPluralCategories,
            "fr" or "pt-BR" or "pt-PT" => s_oneManyOtherPluralCategories,
            "ru" => s_russianPluralCategories,
            "ja" or "vi" or "zh-CN" => s_otherPluralCategory,
            _ => null,
        };

    private static bool ValidatePattern(
        LocalizationCatalog catalog,
        LocalizationMessageDocument message,
        string pattern,
        Dictionary<string, string> arguments,
        HashSet<string> usedArguments,
        SourceProductionContext context)
    {
        if (pattern.Any(char.IsControl))
        {
            ReportInvalid(context, catalog.Path, $"message '{message.Id}' contains a control character");
            return false;
        }

        if (!LocalizationPatternParser.TryParse(pattern, out var parts, out var error))
        {
            ReportInvalid(context, catalog.Path, $"message '{message.Id}' {error}");
            return false;
        }

        var valid = true;
        foreach (var part in parts.Where(static part => part.IsArgument))
        {
            if (!arguments.ContainsKey(part.Text))
            {
                ReportInvalid(context, catalog.Path, $"message '{message.Id}' references undeclared argument '{part.Text}'");
                valid = false;
            }
            else
            {
                usedArguments.Add(part.Text);
            }
        }

        return valid;
    }

    private static bool ValidatePresentation(
        LocalizationCatalog catalog,
        LocalizationMessageDocument message,
        List<string> patterns,
        HashSet<string> accelerators,
        SourceProductionContext context)
    {
        var valid = true;
        if (message.WidthPolicy is null || !s_widthPolicies.Contains(message.WidthPolicy))
        {
            ReportInvalid(context, catalog.Path, $"message '{message.Id}' widthPolicy must be 'wrap', 'clip', or 'hard'");
            valid = false;
        }
        else if (message.WidthPolicy == "hard")
        {
            if (message.MaximumColumns is not > 0)
            {
                ReportInvalid(context, catalog.Path, $"message '{message.Id}' with hard width requires a positive maximumColumns");
                valid = false;
            }
            else if (patterns.Any(pattern => pattern.Length > message.MaximumColumns.Value))
            {
                ReportInvalid(context, catalog.Path, $"message '{message.Id}' exceeds its {message.MaximumColumns}-column hard limit");
                valid = false;
            }
        }
        else if (message.MaximumColumns is not null)
        {
            ReportInvalid(context, catalog.Path, $"message '{message.Id}' may set maximumColumns only with hard width");
            valid = false;
        }

        if (message.Accelerator is null && message.Menu is null)
        {
            return valid;
        }

        if (message.Accelerator is null || message.Menu is null ||
            StringInfo.ParseCombiningCharacters(message.Accelerator).Length != 1)
        {
            ReportInvalid(context, catalog.Path, $"message '{message.Id}' must pair one accelerator text element with a menu scope");
            return false;
        }

        var acceleratorKey = $"{message.Menu}\0{message.Accelerator}";
        if (!accelerators.Add(acceleratorKey))
        {
            ReportInvalid(context, catalog.Path, $"menu '{message.Menu}' repeats accelerator '{message.Accelerator}'");
            valid = false;
        }

        if (!patterns.Any(pattern => pattern.Contains(message.Accelerator, StringComparison.OrdinalIgnoreCase)))
        {
            ReportInvalid(context, catalog.Path, $"message '{message.Id}' accelerator '{message.Accelerator}' is absent from its text");
            valid = false;
        }

        return valid;
    }

    private static bool ValidateCompatibility(
        LocalizationCatalog catalog,
        Dictionary<string, LocalizationMessageDocument> englishMessages,
        SourceProductionContext context)
    {
        var messages = catalog.Document.Messages!
            .ToDictionary(static message => message.Id!, StringComparer.Ordinal);
        var valid = true;
        foreach (var english in englishMessages)
        {
            if (!messages.TryGetValue(english.Key, out var translation))
            {
                ReportIncompatible(context, catalog.Path, english.Key, "message is missing");
                valid = false;
                continue;
            }

            var englishArguments = english.Value.Arguments ?? new Dictionary<string, string>(StringComparer.Ordinal);
            var translatedArguments = translation.Arguments ?? new Dictionary<string, string>(StringComparer.Ordinal);
            if (englishArguments.Count != translatedArguments.Count || englishArguments.Any(argument =>
                !translatedArguments.TryGetValue(argument.Key, out var kind) || kind != argument.Value))
            {
                ReportIncompatible(context, catalog.Path, english.Key, "named argument names or types differ");
                valid = false;
            }

            if (english.Value.Selector?.Kind != translation.Selector?.Kind ||
                english.Value.Selector?.Argument != translation.Selector?.Argument)
            {
                ReportIncompatible(context, catalog.Path, english.Key, "selector kind or argument differs");
                valid = false;
            }

            if (english.Value.Selector?.Kind == "select" &&
                !new HashSet<string>(english.Value.Variants!.Keys, StringComparer.Ordinal)
                    .SetEquals(translation.Variants!.Keys))
            {
                ReportIncompatible(context, catalog.Path, english.Key, "select variant keys differ");
                valid = false;
            }

            if (english.Value.WidthPolicy != translation.WidthPolicy)
            {
                ReportIncompatible(context, catalog.Path, english.Key, "width policy differs");
                valid = false;
            }

            if (english.Value.Menu != translation.Menu ||
                (english.Value.Accelerator is null) != (translation.Accelerator is null))
            {
                ReportIncompatible(context, catalog.Path, english.Key, "accelerator presence or menu scope differs");
                valid = false;
            }
        }

        foreach (var translation in messages.Keys.Except(englishMessages.Keys, StringComparer.Ordinal))
        {
            ReportIncompatible(context, catalog.Path, translation, "message is not present in English");
            valid = false;
        }

        return valid;
    }

    private static bool IsValidLocale(string? locale)
    {
        if (string.IsNullOrEmpty(locale))
        {
            return false;
        }

        var parts = locale!.Split('-');
        if (parts.Length is < 1 or > 2 || parts[0].Length is < 2 or > 3 || !parts[0].All(IsAsciiLower))
        {
            return false;
        }

        return parts.Length == 1 ||
            (parts[1].Length == 2 && parts[1].All(IsAsciiUpper));
    }

    private static bool IsValidMessageId(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return false;
        }

        var segments = id!.Split('.');
        return segments.Length >= 2 && segments.All(static segment =>
            segment.Length > 0 && IsAsciiLower(segment[0]) &&
            segment.All(static value => IsAsciiLower(value) || value is >= '0' and <= '9' or '-'));
    }

    private static bool IsAsciiLower(char value) => value is >= 'a' and <= 'z';

    private static bool IsAsciiUpper(char value) => value is >= 'A' and <= 'Z';

    private static void ReportInvalid(SourceProductionContext context, string path, string message)
        => context.ReportDiagnostic(Diagnostic.Create(
            LocalizationDiagnosticDescriptors.InvalidCatalog,
            CreateLocation(path),
            path,
            message));

    private static void ReportIncompatible(
        SourceProductionContext context,
        string path,
        string messageId,
        string message)
        => context.ReportDiagnostic(Diagnostic.Create(
            LocalizationDiagnosticDescriptors.IncompatibleCatalog,
            CreateLocation(path),
            path,
            messageId,
            message));

    private static Location CreateLocation(string path)
        => Location.Create(
            path,
            new TextSpan(0, 0),
            new LinePositionSpan(new LinePosition(0, 0), new LinePosition(0, 0)));
}
