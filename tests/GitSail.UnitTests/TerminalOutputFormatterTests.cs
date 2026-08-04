using GitSail.Ui;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies general Git output keeps line structure while terminal controls become visible text.
/// </summary>
[TestClass]
public sealed class TerminalOutputFormatterTests
{
    /// <summary>
    /// Verifies carriage-return progress, tabs, newlines, and terminal escape bytes render safely.
    /// </summary>
    [TestMethod]
    public void Format_WithProgressAndControls_PreservesReadableStructure()
    {
        var formatted = TerminalOutputFormatter.Format(
            "checking\r50%\r\ncomplete\tvalue\n\u001b[31m"u8);

        Assert.AreEqual(
            "checking\n50%\ncomplete\tvalue\n<U+001B>[31m",
            formatted);
    }

    /// <summary>
    /// Verifies repository-care output labels empty and nonempty channels independently.
    /// </summary>
    [TestMethod]
    public void SetOutput_WithSeparateChannels_PreservesBothLabelsAndReadOnlyState()
    {
        var state = new RepositoryMaintenanceState();

        state.SetOutput("Verification", "ordinary output\n"u8, []);

        Assert.AreEqual("Verification", state.OutputTitle);
        Assert.IsTrue(state.Output.IsReadOnly);
        var text = state.Output.Document.GetText();
        StringAssert.Contains(text, "standard output:\nordinary output");
        StringAssert.Contains(text, "standard error:\n<empty>");
    }
}
