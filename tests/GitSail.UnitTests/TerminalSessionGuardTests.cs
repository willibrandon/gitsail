using GitSail.Ui;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies redirected terminal directions are rejected before the terminal driver starts.
/// </summary>
[TestClass]
public sealed class TerminalSessionGuardTests
{
    /// <summary>
    /// Verifies only attached standard input and output form an interactive terminal session.
    /// </summary>
    /// <param name="inputRedirected">Whether standard input is redirected.</param>
    /// <param name="outputRedirected">Whether standard output is redirected.</param>
    /// <param name="expected">Whether the terminal UI may start.</param>
    [TestMethod]
    [DataRow(false, false, true)]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    [DataRow(true, true, false)]
    public void IsInteractive_WithTerminalDirections_ReturnsExpectedResult(
        bool inputRedirected,
        bool outputRedirected,
        bool expected)
    {
        var result = TerminalSessionGuard.IsInteractive(inputRedirected, outputRedirected);

        Assert.AreEqual(expected, result);
    }
}
