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
    /// Verifies a mouse report split after Escape is reassembled instead of leaking its text tail.
    /// </summary>
    [TestMethod]
    public void Filter_WithEscapePrefixInEarlierBlock_PreservesCompleteInput()
    {
        var sanitizer = new TerminalMouseInputSanitizer();

        Assert.IsTrue(sanitizer.Filter("\u001b"u8).IsEmpty);
        Assert.IsTrue(sanitizer.HasPendingInput);
        var filtered = sanitizer.Filter("[<35;107;13M"u8);

        Assert.AreEqual("\u001b[<35;107;13M", Encoding.UTF8.GetString(filtered.Span));
        Assert.IsFalse(sanitizer.HasPendingInput);
    }

    /// <summary>
    /// Verifies every read boundary reassembles escaped reports and removes bare reports exactly.
    /// </summary>
    [TestMethod]
    public void Filter_WithEveryReportSplitBoundary_HandlesCompleteSequence()
    {
        AssertEverySplit("\u001b[<35;107;13M", preserve: true);
        AssertEverySplit("[<35;107;13M", preserve: false);

        static void AssertEverySplit(string report, bool preserve)
        {
            var bytes = Encoding.UTF8.GetBytes(report);
            for (var split = 1; split < bytes.Length; split++)
            {
                var sanitizer = new TerminalMouseInputSanitizer();
                var first = sanitizer.Filter(bytes.AsSpan(0, split));
                var second = sanitizer.Filter(bytes.AsSpan(split));
                var combined = first.ToArray().Concat(second.ToArray()).ToArray();

                Assert.AreEqual(
                    preserve ? report : string.Empty,
                    Encoding.UTF8.GetString(combined),
                    $"Unexpected result at split byte {split} for '{report}'.");
                Assert.IsFalse(sanitizer.HasPendingInput);
            }
        }
    }

    /// <summary>
    /// Verifies a standalone Escape is returned unchanged when its continuation wait expires.
    /// </summary>
    [TestMethod]
    public void FlushPendingInput_AfterStandaloneEscape_ReturnsEscape()
    {
        var sanitizer = new TerminalMouseInputSanitizer();

        Assert.IsTrue(sanitizer.Filter("\u001b"u8).IsEmpty);
        var filtered = sanitizer.FlushPendingInput();

        Assert.AreEqual("\u001b", Encoding.UTF8.GetString(filtered.Span));
        Assert.IsFalse(sanitizer.HasPendingInput);
    }

    /// <summary>
    /// Verifies a recognized report can be discarded without changing later keyboard text.
    /// </summary>
    [TestMethod]
    public void DiscardPendingMouseReport_AfterRecognizedPrefix_PreservesLaterInput()
    {
        var sanitizer = new TerminalMouseInputSanitizer();

        Assert.IsTrue(sanitizer.Filter("[<35;107;"u8).IsEmpty);
        Assert.IsTrue(sanitizer.HasRecognizedMouseReport);
        sanitizer.DiscardPendingMouseReport();
        var filtered = sanitizer.Filter("main"u8);

        Assert.AreEqual("main", Encoding.UTF8.GetString(filtered.Span));
        Assert.IsFalse(sanitizer.HasPendingInput);
    }

    /// <summary>
    /// Verifies ordinary bracket input and invalid lookalikes remain ordinary keyboard text.
    /// </summary>
    [TestMethod]
    public void Filter_WithOrdinaryBracketText_PreservesCompleteInput()
    {
        var sanitizer = new TerminalMouseInputSanitizer();

        Assert.IsTrue(sanitizer.Filter("["u8).IsEmpty);
        var bracket = sanitizer.FlushPendingInput();
        var lookalike = sanitizer.Filter("[<feature"u8);

        Assert.AreEqual("[", Encoding.UTF8.GetString(bracket.Span));
        Assert.AreEqual("[<feature", Encoding.UTF8.GetString(lookalike.Span));
    }
}
