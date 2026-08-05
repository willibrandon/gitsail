using Hex1b;
using System.Text.Json;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies localized hard-width text fits its declared terminal-cell budget.
/// </summary>
[TestClass]
public sealed class LocalizationCatalogLayoutTests
{
    /// <summary>
    /// Verifies every required catalog is present and every hard-width pattern fits in terminal cells.
    /// </summary>
    [TestMethod]
    public void HardWidthMessages_AcrossRequiredCatalogs_FitDeclaredTerminalColumns()
    {
        var localeDirectory = Path.Combine(AppContext.BaseDirectory, "locales");
        var catalogPaths = Directory.GetFiles(localeDirectory, "*.json").Order(StringComparer.Ordinal).ToArray();
        Assert.HasCount(15, catalogPaths);
        using var englishStream = File.OpenRead(Path.Combine(localeDirectory, "en.json"));
        using var englishCatalog = JsonDocument.Parse(englishStream);
        var englishContracts = englishCatalog.RootElement
            .GetProperty("messages")
            .EnumerateArray()
            .ToDictionary(
                static message => message.GetProperty("id").GetString()!,
                static message => (
                    WidthPolicy: message.GetProperty("widthPolicy").GetString()!,
                    MaximumColumns: message.TryGetProperty("maximumColumns", out var maximumColumns)
                        ? maximumColumns.GetInt32()
                        : (int?)null),
                StringComparer.Ordinal);
        var failures = new List<string>();

        foreach (var catalogPath in catalogPaths)
        {
            using var stream = File.OpenRead(catalogPath);
            using var catalog = JsonDocument.Parse(stream);
            var locale = catalog.RootElement.GetProperty("locale").GetString();
            foreach (var message in catalog.RootElement.GetProperty("messages").EnumerateArray())
            {
                var messageId = message.GetProperty("id").GetString()!;
                var englishContract = englishContracts[messageId];
                var widthPolicy = message.TryGetProperty("widthPolicy", out var translatedWidthPolicy)
                    ? translatedWidthPolicy.GetString()
                    : englishContract.WidthPolicy;
                if (widthPolicy != "hard")
                {
                    continue;
                }

                var maximumColumns = message.TryGetProperty("maximumColumns", out var translatedMaximumColumns)
                    ? translatedMaximumColumns.GetInt32()
                    : englishContract.MaximumColumns!.Value;
                var text = message.GetProperty("text").GetString()!;
                var width = DisplayWidth.GetStringWidth(text);
                if (width > maximumColumns)
                {
                    failures.Add(
                        $"Locale '{locale}' message '{messageId}' occupies {width} terminal columns " +
                        $"but declares {maximumColumns}.");
                }
            }
        }

        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }
}
