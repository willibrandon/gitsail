using GitSail.Ui;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies leaked Windows SGR mouse reports never become repository filter text.
/// </summary>
[TestClass]
public sealed class TerminalMouseReportFilterTests
{
    /// <summary>
    /// Verifies a complete mouse report is removed without changing surrounding user text.
    /// </summary>
    [TestMethod]
    public void Filter_WithCompleteMouseReport_RemovesOnlyReport()
    {
        var filter = new TerminalMouseReportFilter();

        Assert.AreEqual("main", filter.Filter("ma[<35;181;4min"));
        Assert.AreEqual(
            string.Empty,
            filter.Filter("[<35;107;13M[<35;83;6M"));
    }

    /// <summary>
    /// Verifies a mouse report split into individual console reads remains completely invisible.
    /// </summary>
    [TestMethod]
    public void Filter_WithFragmentedMouseReport_DiscardsThroughTerminator()
    {
        var filter = new TerminalMouseReportFilter();

        Assert.AreEqual("[", filter.Filter("["));
        Assert.AreEqual("[<", filter.Filter("[<"));
        Assert.AreEqual(string.Empty, filter.Filter("[<35;"));
        Assert.AreEqual(string.Empty, filter.Filter("181;"));
        Assert.AreEqual(string.Empty, filter.Filter("4"));
        Assert.AreEqual(string.Empty, filter.Filter("m"));
        Assert.AreEqual("feature", filter.Filter("feature"));
    }

    /// <summary>
    /// Verifies ordinary branch-filter punctuation remains unchanged.
    /// </summary>
    [TestMethod]
    public void Filter_WithOrdinaryText_ReturnsOriginalText()
    {
        var filter = new TerminalMouseReportFilter();

        Assert.AreEqual("feature[preview];4", filter.Filter("feature[preview];4"));
    }
}
