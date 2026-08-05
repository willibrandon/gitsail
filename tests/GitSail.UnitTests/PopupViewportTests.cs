using GitSail.Ui;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies popup dimensions retain their preference while staying inside the terminal viewport.
/// </summary>
[TestClass]
public sealed class PopupViewportTests
{
    /// <summary>
    /// Verifies preferred dimensions remain unchanged before a root layout is available.
    /// </summary>
    [TestMethod]
    public void Fit_WithoutCapturedViewport_ReturnsPreferredDimensions()
    {
        var viewport = new PopupViewport();

        Assert.AreEqual(78, viewport.FitWidth(78));
        Assert.AreEqual(22, viewport.FitHeight(22));
    }

    /// <summary>
    /// Verifies the supported minimum terminal retains a one-cell margin around each popup.
    /// </summary>
    [TestMethod]
    public void Fit_WithMinimumSupportedViewport_LeavesOneCellMargin()
    {
        var viewport = new PopupViewport();

        Assert.IsTrue(viewport.Capture(60, 18));

        Assert.AreEqual(58, viewport.FitWidth(78));
        Assert.AreEqual(16, viewport.FitHeight(22));
        Assert.AreEqual(40, viewport.FitWidth(40));
        Assert.AreEqual(10, viewport.FitHeight(10));
    }

    /// <summary>
    /// Verifies extremely small terminals still produce positive bounded popup dimensions.
    /// </summary>
    [TestMethod]
    public void Fit_WithTinyViewport_ReturnsPositiveDimensions()
    {
        var viewport = new PopupViewport();

        Assert.IsTrue(viewport.Capture(1, 1));

        Assert.AreEqual(1, viewport.FitWidth(78));
        Assert.AreEqual(1, viewport.FitHeight(22));
    }
}
