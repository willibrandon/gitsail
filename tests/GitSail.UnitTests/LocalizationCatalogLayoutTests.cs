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

        foreach (var catalogPath in catalogPaths)
        {
            using var stream = File.OpenRead(catalogPath);
            using var catalog = JsonDocument.Parse(stream);
            var locale = catalog.RootElement.GetProperty("locale").GetString();
            foreach (var message in catalog.RootElement.GetProperty("messages").EnumerateArray())
            {
                if (message.GetProperty("widthPolicy").GetString() != "hard")
                {
                    continue;
                }

                var messageId = message.GetProperty("id").GetString();
                var maximumColumns = message.GetProperty("maximumColumns").GetInt32();
                var text = message.GetProperty("text").GetString()!;
                var width = DisplayWidth.GetStringWidth(text);
                Assert.IsLessThanOrEqualTo(
                    maximumColumns,
                    width,
                    $"Locale '{locale}' message '{messageId}' occupies {width} terminal columns.");
            }
        }
    }
}
