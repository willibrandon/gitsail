using GitSail.Ui;
using Hex1b.Documents;
using System.Collections.Immutable;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies semantic comparison gutter sizing for old, new, and dual columns.
/// </summary>
[TestClass]
public sealed class ComparisonLineNumberGutterProviderTests
{
    /// <summary>
    /// Verifies gutter widths retain every digit in the largest repository line number.
    /// </summary>
    [TestMethod]
    public void GetWidth_WithThreeDigitCoordinates_AllocatesCompleteColumns()
    {
        var lineNumbers = ImmutableArray.Create(
            new ComparisonLineNumber(120, 98));
        var document = new Hex1bDocument("line\n");
        var old = new ComparisonLineNumberGutterProvider(
            lineNumbers,
            showOld: true,
            showNew: false);
        var added = new ComparisonLineNumberGutterProvider(
            lineNumbers,
            showOld: false,
            showNew: true);
        var dual = new ComparisonLineNumberGutterProvider(
            lineNumbers,
            showOld: true,
            showNew: true);

        Assert.AreEqual(4, old.GetWidth(document));
        Assert.AreEqual(4, added.GetWidth(document));
        Assert.AreEqual(8, dual.GetWidth(document));
    }
}
