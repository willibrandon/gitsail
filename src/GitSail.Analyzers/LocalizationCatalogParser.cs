using System.Runtime.Serialization.Json;
using System.Text;

namespace GitSail.Analyzers;

/// <summary>
/// Parses one bounded JSON localization catalog without adding a runtime application dependency.
/// </summary>
internal static class LocalizationCatalogParser
{
    private const int MaximumCatalogCharacters = 4 * 1024 * 1024;

    /// <summary>
    /// Parses one additional file into its catalog document.
    /// </summary>
    /// <param name="input">The catalog source.</param>
    /// <returns>The parsed catalog document.</returns>
    /// <exception cref="InvalidDataException">The catalog exceeds the build-time size limit or cannot be decoded.</exception>
    internal static LocalizationCatalogDocument Parse(LocalizationCatalogInput input)
    {
        if (input.Text.Length > MaximumCatalogCharacters)
        {
            throw new InvalidDataException($"catalog exceeds {MaximumCatalogCharacters} characters");
        }

        var bytes = Encoding.UTF8.GetBytes(input.Text.ToString());
        using var stream = new MemoryStream(bytes, writable: false);
        var serializer = new DataContractJsonSerializer(
            typeof(LocalizationCatalogDocument),
            new DataContractJsonSerializerSettings
            {
                UseSimpleDictionaryFormat = true,
            });
        return serializer.ReadObject(stream) as LocalizationCatalogDocument ??
            throw new InvalidDataException("catalog root must be a JSON object");
    }
}
