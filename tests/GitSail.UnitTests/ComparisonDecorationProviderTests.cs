using GitSail.Ui;
using Hex1b.Documents;
using Hex1b.Theming;
using System.Collections.Immutable;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies full-line comparison colors and higher-priority intraline emphasis.
/// </summary>
[TestClass]
public sealed class ComparisonDecorationProviderTests
{
    /// <summary>
    /// Verifies an addition keeps its complete line tint beneath the exact changed range.
    /// </summary>
    [TestMethod]
    public void GetDecorations_WithAdditionHighlight_ReturnsLayeredSpans()
    {
        var provider = new ComparisonDecorationProvider(
            ImmutableArray.Create(new ComparisonHighlight(1, 2, 5, IsAddition: true)));
        var document = new Hex1bDocument("+new\n");

        var spans = provider.GetDecorations(1, 1, document);

        Assert.HasCount(2, spans);
        var baseline = spans.Single(span => span.Priority == 0);
        Assert.AreEqual(1, baseline.Start.Column);
        Assert.AreEqual(5, baseline.End.Column);
        Assert.AreEqual(Hex1bColor.FromRgb(20, 40, 20), baseline.Decoration.Background);
        var intraline = spans.Single(span => span.Priority == 100);
        Assert.AreEqual(2, intraline.Start.Column);
        Assert.AreEqual(5, intraline.End.Column);
        Assert.AreEqual(Hex1bColor.FromRgb(35, 85, 35), intraline.Decoration.Background);
        Assert.IsTrue(intraline.Decoration.Bold);
    }
}
