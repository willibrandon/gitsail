using System.Text;
using GitSail.Ui;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies malformed Windows mouse reports are removed before terminal input decoding.
/// </summary>
[TestClass]
public sealed class TerminalMouseInputSanitizerTests
{
    /// <summary>
    /// Verifies the exact leaked reports from a real Windows session are discarded.
    /// </summary>
    [TestMethod]
    public void Filter_WithCapturedBareReports_RemovesEveryReport()
    {
        var sanitizer = new TerminalMouseInputSanitizer();

        var filtered = sanitizer.Filter("[<35;107;13M[<35;83;6M"u8);

        Assert.IsTrue(filtered.IsEmpty);
    }

    /// <summary>
    /// Verifies fragmented reports are discarded without consuming later keyboard input.
    /// </summary>
    [TestMethod]
    public void Filter_WithFragmentedReport_ReturnsOnlyFollowingKeyboardInput()
    {
        var sanitizer = new TerminalMouseInputSanitizer();

        Assert.IsTrue(sanitizer.Filter("[<35;"u8).IsEmpty);
        Assert.IsTrue(sanitizer.Filter("107;"u8).IsEmpty);
        var filtered = sanitizer.Filter("13Mmain"u8);

        Assert.AreEqual("main", Encoding.UTF8.GetString(filtered.Span));
    }

    /// <summary>
    /// Verifies valid escape-prefixed mouse input remains available to the mouse decoder.
    /// </summary>
    [TestMethod]
    public void Filter_WithEscapePrefixedReport_PreservesCompleteInput()
    {
        var sanitizer = new TerminalMouseInputSanitizer();
        const string input = "\u001b[<35;107;13M";

        var filtered = sanitizer.Filter(Encoding.UTF8.GetBytes(input));

        Assert.AreEqual(input, Encoding.UTF8.GetString(filtered.Span));
    }

    /// <summary>
    /// Verifies ordinary bracket input and invalid lookalikes remain ordinary keyboard text.
    /// </summary>
    [TestMethod]
    public void Filter_WithOrdinaryBracketText_PreservesCompleteInput()
    {
        var sanitizer = new TerminalMouseInputSanitizer();

        var bracket = sanitizer.Filter("["u8);
        var lookalike = sanitizer.Filter("[<feature"u8);

        Assert.AreEqual("[", Encoding.UTF8.GetString(bracket.Span));
        Assert.AreEqual("[<feature", Encoding.UTF8.GetString(lookalike.Span));
    }
}
