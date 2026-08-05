using Hex1b;
using Hex1b.Tokens;

namespace GitSail.PerformanceTests;

/// <summary>
/// Verifies detection of the first complete interactive GitSail frame.
/// </summary>
[TestClass]
public sealed class FirstInteractiveFrameFilterTests
{
    /// <summary>
    /// Verifies the provisional repository-loading screen is not measured as interactive.
    /// </summary>
    [TestMethod]
    public async Task OnOutputAsync_WithLoadingScreen_DoesNotCompleteMeasurement()
    {
        var filter = new FirstInteractiveFrameFilter();

        _ = await ApplyTextAsync(
            filter,
            "GitSail Opening repository Quit",
            TimeSpan.FromMilliseconds(10));

        Assert.IsFalse(filter.FirstFrame.IsCompleted);
    }

    /// <summary>
    /// Verifies the detector completes when all interactive workspace regions have rendered.
    /// </summary>
    [TestMethod]
    public async Task OnOutputAsync_WithCompleteWorkspace_CompletesAtFinalRegion()
    {
        var filter = new FirstInteractiveFrameFilter();
        _ = await ApplyTextAsync(
            filter,
            "GitSail Unstaged Staged Diff Commit message",
            TimeSpan.FromMilliseconds(10));
        Assert.IsFalse(filter.FirstFrame.IsCompleted);

        _ = await ApplyTextAsync(filter, "Quit", TimeSpan.FromMilliseconds(15));

        Assert.AreEqual(TimeSpan.FromMilliseconds(15), await filter.FirstFrame);
    }

    private static ValueTask<IReadOnlyList<AnsiToken>> ApplyTextAsync(
        FirstInteractiveFrameFilter filter,
        string text,
        TimeSpan elapsed)
        => ((IHex1bTerminalPresentationFilter)filter).OnOutputAsync(
            [AppliedToken.WithNoCellImpacts(new TextToken(text), 0, 0, 0, 0)],
            elapsed,
            CancellationToken.None);
}
