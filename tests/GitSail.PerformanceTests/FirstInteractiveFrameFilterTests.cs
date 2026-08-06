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

    /// <summary>
    /// Verifies PTY output represented by rendered cell impacts is detected without literal text tokens.
    /// </summary>
    [TestMethod]
    public async Task OnOutputAsync_WithCompleteImpactedSurface_CompletesMeasurement()
    {
        const string workspace = "GitSail Unstaged Staged Diff Commit message Quit";
        var filter = new FirstInteractiveFrameFilter();
        await ((IHex1bTerminalPresentationFilter)filter).OnSessionStartAsync(
            workspace.Length,
            1,
            DateTimeOffset.UnixEpoch,
            CancellationToken.None);
        var impacts = workspace.Select((character, index) => new CellImpact(
            index,
            0,
            new TerminalCell(character.ToString(), null, null))).ToArray();

        _ = await ((IHex1bTerminalPresentationFilter)filter).OnOutputAsync(
            [new AppliedToken(new TextToken(string.Empty), impacts, 0, 0, 0, 0)],
            TimeSpan.FromMilliseconds(12),
            CancellationToken.None);

        Assert.AreEqual(TimeSpan.FromMilliseconds(12), await filter.FirstFrame);
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
