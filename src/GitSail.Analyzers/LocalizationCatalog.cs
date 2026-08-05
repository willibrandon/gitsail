namespace GitSail.Analyzers;

/// <summary>
/// Associates a validated catalog document with its source path.
/// </summary>
internal sealed class LocalizationCatalog
{
    /// <summary>
    /// Initializes one parsed localization catalog.
    /// </summary>
    /// <param name="path">The source catalog path.</param>
    /// <param name="document">The parsed catalog document.</param>
    internal LocalizationCatalog(string path, LocalizationCatalogDocument document)
    {
        Path = path;
        Document = document;
    }

    /// <summary>
    /// Gets the source catalog path.
    /// </summary>
    internal string Path { get; }

    /// <summary>
    /// Gets the parsed catalog document.
    /// </summary>
    internal LocalizationCatalogDocument Document { get; }
}
