using GitSail.Ui;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;

namespace GitSail.UiTests;

/// <summary>
/// Verifies full-screen terminal sessions restore the calling shell after exit.
/// </summary>
[TestClass]
public sealed class TerminalApplicationSessionTests
{
    /// <summary>
    /// Verifies Ctrl+Q drains pending frames before restoring the main terminal screen.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_WithCtrlQ_RestoresScreenAfterAllQueuedOutput()
    {
        const string remnant = "GitSail shutdown remnant";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var session = new TerminalApplicationSession(
            context => context.VStack(builder =>
                [
                    builder.Text(remnant),
                    builder.Text("Ctrl+Q exits"),
                ]).InputBindings(bindings =>
                {
                    bindings.Ctrl().Key(Hex1bKey.Q).Action(
                        actionContext => actionContext.RequestStop(),
                        "Quit test application");
                }).Fill(),
            new Hex1bAppOptions
            {
                EnableMouse = true,
                EnableDefaultCtrlCExit = true,
            },
            new DelayedPresentationAdapter(
                80,
                24,
                TimeSpan.FromMilliseconds(50)));
        var automator = new Hex1bTerminalAutomator(session.Terminal, TimeSpan.FromSeconds(5));
        var runTask = session.RunAsync(timeout.Token);

        await automator.WaitUntilAlternateScreenAsync(TimeSpan.FromSeconds(5));
        await automator.WaitUntilTextAsync(remnant, TimeSpan.FromSeconds(5));
        await automator.Ctrl().KeyAsync(Hex1bKey.Q, timeout.Token);
        await runTask.WaitAsync(timeout.Token);

        using var restored = automator.CreateSnapshot();
        Assert.IsFalse(restored.InAlternateScreen);
        Assert.IsFalse(restored.ContainsText(remnant));
    }

    /// <summary>
    /// Verifies a clean repaint replaces a long frame without retaining its old suffix.
    /// </summary>
    [TestMethod]
    public async Task RequestCleanRepaint_AfterContentShrinks_RemovesOldSuffix()
    {
        const string oldSuffix = "old-history-preview-tail-}],";
        var content = $"Current preview {new string('x', 40)} {oldSuffix}";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var session = new TerminalApplicationSession(
            context => context.VStack(builder =>
                [
                    builder.Text(Volatile.Read(ref content)),
                    builder.Text("Ctrl+Q exits"),
                ]).InputBindings(bindings =>
                {
                    bindings.Ctrl().Key(Hex1bKey.Q).Action(
                        actionContext => actionContext.RequestStop(),
                        "Quit test application");
                }).Fill(),
            new Hex1bAppOptions
            {
                EnableMouse = true,
                EnableDefaultCtrlCExit = true,
            },
            new DelayedPresentationAdapter(
                100,
                24,
                TimeSpan.FromMilliseconds(10)));
        var automator = new Hex1bTerminalAutomator(session.Terminal, TimeSpan.FromSeconds(5));
        var runTask = session.RunAsync(timeout.Token);

        await automator.WaitUntilTextAsync(oldSuffix, TimeSpan.FromSeconds(5));
        Volatile.Write(ref content, "Current preview is short.");
        session.RequestCleanRepaint();
        await automator.WaitUntilTextAsync("Current preview is short.", TimeSpan.FromSeconds(5));
        await automator.WaitUntilAsync(
            snapshot => !snapshot.ContainsText(oldSuffix),
            TimeSpan.FromSeconds(5),
            "Clean repaint removes every cell from the prior longer frame");
        await automator.Ctrl().KeyAsync(Hex1bKey.Q, timeout.Token);
        await runTask.WaitAsync(timeout.Token);

        using var restored = automator.CreateSnapshot();
        Assert.IsFalse(restored.InAlternateScreen);
        Assert.IsFalse(restored.ContainsText(oldSuffix));
    }
}
