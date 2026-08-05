namespace GitSail.Analyzers.Tests;

/// <summary>
/// Verifies generated pseudo-locales stress layout and bidirectional isolation safely.
/// </summary>
[TestClass]
public sealed class PseudoLocalizationTransformerTests
{
    /// <summary>
    /// Verifies expansion pseudo-localization accents and lengthens text without changing argument markers.
    /// </summary>
    [TestMethod]
    public void Transform_WithExpansion_PreservesNamedArgumentsAndExpandsText()
    {
        const string source = "Loaded { $count } files";

        var transformed = PseudoLocalizationTransformer.Transform(source, rightToLeft: false);

        Assert.StartsWith("⟦", transformed);
        Assert.EndsWith("~~⟧", transformed);
        Assert.Contains("{ $count }", transformed);
        Assert.IsGreaterThan(source.Length, transformed.Length);
    }

    /// <summary>
    /// Verifies RTL pseudo-localization reverses presentation parts inside one isolation pair.
    /// </summary>
    [TestMethod]
    public void Transform_WithRightToLeft_PreservesNamedArgumentsAndAddsIsolation()
    {
        var transformed = PseudoLocalizationTransformer.Transform(
            "Loaded { $count } files",
            rightToLeft: true);

        Assert.StartsWith("\u2067⟦", transformed);
        Assert.EndsWith("⟧\u2069", transformed);
        Assert.Contains("{ $count }", transformed);
        Assert.IsLessThan(
            transformed.IndexOf("Ļ", StringComparison.Ordinal),
            transformed.IndexOf("{ $count }", StringComparison.Ordinal));
    }
}
