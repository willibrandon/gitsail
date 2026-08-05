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
    /// Verifies required one-other locales use the singular form only for one.
    /// </summary>
    /// <param name="locale">The normalized locale.</param>
    [TestMethod]
    [DataRow("bg")]
    [DataRow("de")]
    [DataRow("el")]
    [DataRow("hu")]
    [DataRow("it")]
    [DataRow("nb")]
    [DataRow("sv")]
    public void GetCategory_WithRequiredOneOtherLocale_ReturnsOneOnlyForOne(string locale)
    {
        Assert.AreEqual(PluralCategory.Other, PluralRules.GetCategory(locale, 0));
        Assert.AreEqual(PluralCategory.One, PluralRules.GetCategory(locale, 1));
        Assert.AreEqual(PluralCategory.Other, PluralRules.GetCategory(locale, 2));
    }

    /// <summary>
    /// Verifies French and Brazilian Portuguese classify zero, one, and whole millions.
    /// </summary>
    /// <param name="locale">The normalized locale.</param>
    [TestMethod]
    [DataRow("fr")]
    [DataRow("pt-BR")]
    public void GetCategory_WithZeroOneAndMillionLocale_ReturnsExpectedCategories(string locale)
    {
        Assert.AreEqual(PluralCategory.One, PluralRules.GetCategory(locale, 0));
        Assert.AreEqual(PluralCategory.One, PluralRules.GetCategory(locale, 1));
        Assert.AreEqual(PluralCategory.Other, PluralRules.GetCategory(locale, 2));
        Assert.AreEqual(PluralCategory.Many, PluralRules.GetCategory(locale, 1_000_000));
        Assert.AreEqual(PluralCategory.Many, PluralRules.GetCategory(locale, 2_000_000));
    }

    /// <summary>
    /// Verifies European Portuguese distinguishes one and whole millions from other counts.
    /// </summary>
    [TestMethod]
    public void GetCategory_WithEuropeanPortugueseCount_ReturnsExpectedCategories()
    {
        Assert.AreEqual(PluralCategory.Other, PluralRules.GetCategory("pt-PT", 0));
        Assert.AreEqual(PluralCategory.One, PluralRules.GetCategory("pt-PT", 1));
        Assert.AreEqual(PluralCategory.Other, PluralRules.GetCategory("pt-PT", 2));
        Assert.AreEqual(PluralCategory.Many, PluralRules.GetCategory("pt-PT", 1_000_000));
    }

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

    /// <summary>
    /// Verifies negative counts always use the safe fallback category.
    /// </summary>
    /// <param name="locale">The normalized locale.</param>
    [TestMethod]
    [DataRow("en")]
    [DataRow("fr")]
    [DataRow("pt-BR")]
    [DataRow("pt-PT")]
    [DataRow("ru")]
    public void GetCategory_WithNegativeCount_ReturnsOther(string locale)
        => Assert.AreEqual(PluralCategory.Other, PluralRules.GetCategory(locale, -1));
}
