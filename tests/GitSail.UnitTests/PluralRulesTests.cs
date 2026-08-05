using GitSail.Localization;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies required-locale cardinal plural categories.
/// </summary>
[TestClass]
public sealed class PluralRulesTests
{
    /// <summary>
    /// Verifies English distinguishes exactly one item.
    /// </summary>
    /// <param name="count">The item count.</param>
    /// <param name="expected">The expected plural category name.</param>
    [TestMethod]
    [DataRow(0, "Other")]
    [DataRow(1, "One")]
    [DataRow(2, "Other")]
    public void GetCategory_WithEnglishCount_ReturnsExpectedCategory(
        int count,
        string expected)
        => Assert.AreEqual(expected, PluralRules.GetCategory("en", count).ToString());

    /// <summary>
    /// Verifies Russian one, few, and many rules around the teen exception.
    /// </summary>
    /// <param name="count">The item count.</param>
    /// <param name="expected">The expected plural category name.</param>
    [TestMethod]
    [DataRow(1, "One")]
    [DataRow(2, "Few")]
    [DataRow(5, "Many")]
    [DataRow(11, "Many")]
    [DataRow(21, "One")]
    [DataRow(22, "Few")]
    public void GetCategory_WithRussianCount_ReturnsExpectedCategory(
        int count,
        string expected)
        => Assert.AreEqual(expected, PluralRules.GetCategory("ru", count).ToString());

    /// <summary>
    /// Verifies languages without cardinal inflection always use the fallback form.
    /// </summary>
    /// <param name="locale">The normalized locale.</param>
    [TestMethod]
    [DataRow("ja")]
    [DataRow("vi")]
    [DataRow("zh-CN")]
    public void GetCategory_WithUninflectedLocale_ReturnsOther(string locale)
        => Assert.AreEqual(PluralCategory.Other, PluralRules.GetCategory(locale, 1));
}
