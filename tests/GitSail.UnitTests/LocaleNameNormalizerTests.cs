using GitSail.Localization;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies locale names normalize deterministically for generated catalog lookup.
/// </summary>
[TestClass]
public sealed class LocaleNameNormalizerTests
{
    /// <summary>
    /// Verifies POSIX encoding and underscore syntax becomes normalized BCP 47 syntax.
    /// </summary>
    /// <param name="input">The source locale name.</param>
    /// <param name="expected">The expected normalized locale.</param>
    [TestMethod]
    [DataRow("pt_BR.UTF-8", "pt-BR")]
    [DataRow("zh_CN.utf8", "zh-CN")]
    [DataRow("fr_FR@euro", "fr-FR")]
    [DataRow("DE_de", "de-DE")]
    [DataRow("en_XA", "en-XA")]
    [DataRow("ar_XB", "ar-XB")]
    public void Normalize_WithPosixLocale_ReturnsBcp47(string input, string expected)
        => Assert.AreEqual(expected, LocaleNameNormalizer.Normalize(input));

    /// <summary>
    /// Verifies invariant POSIX locale names use the English resilience fallback.
    /// </summary>
    /// <param name="input">The invariant locale name.</param>
    [TestMethod]
    [DataRow("C")]
    [DataRow("POSIX")]
    [DataRow("")]
    public void Normalize_WithInvariantLocale_ReturnsEnglish(string input)
        => Assert.AreEqual("en", LocaleNameNormalizer.Normalize(input));
}
