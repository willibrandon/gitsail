namespace GitSail.Analyzers.Tests;

/// <summary>
/// Verifies the release locale set remains complete and deterministic.
/// </summary>
[TestClass]
public sealed class RequiredLocaleSetTests
{
    /// <summary>
    /// Verifies the required locale set exactly matches the design contract.
    /// </summary>
    [TestMethod]
    public void Names_ReturnsCompleteReleaseLocaleSet()
    {
        string[] expected =
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

        CollectionAssert.AreEqual(expected, RequiredLocaleSet.Names.ToArray());
    }

    /// <summary>
    /// Verifies coverage checking identifies only absent required locales.
    /// </summary>
    [TestMethod]
    public void FindMissing_WithOneOmittedLocale_ReturnsOmittedLocale()
    {
        string[] expected = ["ru"];
        var actual = RequiredLocaleSet.FindMissing(
            RequiredLocaleSet.Names.Where(static locale => locale != "ru"));

        CollectionAssert.AreEqual(expected, actual.ToArray());
    }
}
