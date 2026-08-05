using Microsoft.CodeAnalysis.Text;

namespace GitSail.Analyzers;

/// <summary>
/// Carries one additional localization file into the incremental generator.
/// </summary>
internal sealed class LocalizationCatalogInput
{
    /// <summary>
    /// Initializes one localization catalog input.
    /// </summary>
    /// <param name="path">The source catalog path.</param>
    /// <param name="text">The complete source catalog text.</param>
    internal LocalizationCatalogInput(string path, SourceText text)
    {
        Path = path;
        Text = text;
    }

    /// <summary>
    /// Gets the source catalog path.
    /// </summary>
    internal string Path { get; }

    /// <summary>
    /// Gets the complete source catalog text.
    /// </summary>
    internal SourceText Text { get; }
}
