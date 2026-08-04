using GitSail.Ui;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies terminal-safe rendering of untrusted repository and child-process text.
/// </summary>
[TestClass]
public sealed class TerminalTextSanitizerTests
{
    /// <summary>
    /// Verifies that ordinary printable text remains unchanged.
    /// </summary>
    [TestMethod]
    public void Sanitize_WithPrintableText_ReturnsOriginalText()
    {
        Assert.AreEqual("branch/main", TerminalTextSanitizer.Sanitize("branch/main"));
    }

    /// <summary>
    /// Verifies that terminal escape, line-break, and bidirectional controls become visible tokens.
    /// </summary>
    [TestMethod]
    public void Sanitize_WithHostileControls_ReturnsVisibleTokens()
    {
        var result = TerminalTextSanitizer.Sanitize("a\u001B\nb\u202Ec");

        Assert.AreEqual("a<U+001B><U+000A>b<U+202E>c", result);
    }
}
