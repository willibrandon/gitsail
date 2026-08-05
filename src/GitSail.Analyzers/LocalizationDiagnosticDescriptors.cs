using Microsoft.CodeAnalysis;

namespace GitSail.Analyzers;

/// <summary>
/// Defines build errors produced for invalid localization catalogs.
/// </summary>
internal static class LocalizationDiagnosticDescriptors
{
    /// <summary>
    /// Identifies malformed catalog JSON or metadata.
    /// </summary>
    internal const string InvalidCatalogId = "GITSAILLOC001";

    /// <summary>
    /// Identifies a translation that is incompatible with the English source catalog.
    /// </summary>
    internal const string IncompatibleCatalogId = "GITSAILLOC002";

    /// <summary>
    /// Gets the malformed-catalog diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor InvalidCatalog { get; } = new(
        InvalidCatalogId,
        "Fix the localization catalog",
        "Localization catalog '{0}' is invalid: {1}",
        "GitSail.Localization",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Localization catalogs must satisfy the GitSail catalog schema and safety rules.");

    /// <summary>
    /// Gets the incompatible-translation diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor IncompatibleCatalog { get; } = new(
        IncompatibleCatalogId,
        "Match the English localization contract",
        "Localization catalog '{0}' is incompatible with English message '{1}': {2}",
        "GitSail.Localization",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Every translation must contain the same messages and typed named arguments as English.");
}
