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
        var presentation = new DelayedPresentationAdapter(
            80,
            24,
            TimeSpan.FromMilliseconds(50));
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
            presentation);
        var automator = new Hex1bTerminalAutomator(session.Terminal, TimeSpan.FromSeconds(5));
        var runTask = session.RunAsync(timeout.Token);

        await automator.WaitUntilAlternateScreenAsync(TimeSpan.FromSeconds(5));
        await automator.WaitUntilTextAsync(remnant, TimeSpan.FromSeconds(5));
        await automator.Ctrl().KeyAsync(Hex1bKey.Q, timeout.Token);
        await runTask.WaitAsync(timeout.Token);

        using var restored = automator.CreateSnapshot();
        Assert.IsFalse(restored.InAlternateScreen);
        Assert.IsFalse(restored.ContainsText(remnant));

        var writes = presentation.CaptureWrites()
            .Select(static write => System.Text.Encoding.UTF8.GetString(write.Span))
            .ToArray();
        var disableAutoWrapIndex = Array.FindIndex(
            writes,
            static write => write.Contains("\x1b[?7l", StringComparison.Ordinal));
        var firstFrameIndex = Array.FindIndex(
            writes,
            static write => write.Contains("\x1b[?2026h", StringComparison.Ordinal));
        Assert.IsGreaterThanOrEqualTo(
            0,
            disableAutoWrapIndex,
            "The full-screen session must disable right-edge wrapping before rendering.");
        Assert.IsGreaterThan(
            disableAutoWrapIndex,
            firstFrameIndex,
            "Automatic wrapping must be disabled before the first synchronized frame.");
        Assert.IsTrue(
            writes[^1].Contains("\x1b[?1049l\x1b[?7h", StringComparison.Ordinal),
            "The ordered exit barrier must leave the alternate screen before restoring automatic wrapping.");
    }

    /// <summary>
    /// Verifies a clean repaint replaces a long frame without retaining its old suffix.
    /// </summary>
    [TestMethod]
    public async Task RequestCleanRepaint_AfterContentShrinks_RemovesOldSuffix()
    {
        const string oldSuffix = "old-history-preview-tail-}],";
        const string synchronizedFrameBegin = "\x1b[?2026h";
        const string synchronizedFrameEnd = "\x1b[?2026l";
        const string cleanScreenModes = "\x1b[?2026l\x1b[?7l\x1b[?25l\x1b[0m";
        const string cleanScreenOverwriteBegin = "\x1b[1;1H";
        var content = $"Current preview {new string('x', 40)} {oldSuffix}";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var presentation = new DelayedPresentationAdapter(
            100,
            24,
            TimeSpan.FromMilliseconds(10));
        var nativeClearCount = 0;
        string? writeBeforeNativeClear = null;
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
            presentation,
            () =>
            {
                Interlocked.Increment(ref nativeClearCount);
                writeBeforeNativeClear = System.Text.Encoding.UTF8.GetString(
                    presentation.CaptureWrites()[^1].Span);
            });
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
        await automator.WaitUntilAsync(
            _ =>
            {
                var capturedWrites = presentation.CaptureWrites()
                    .Select(static write => System.Text.Encoding.UTF8.GetString(write.Span))
                    .ToArray();
                var modesIndex = Array.FindIndex(
                    capturedWrites,
                    write => write.Equals(cleanScreenModes, StringComparison.Ordinal));
                var replacementFrameIndex = modesIndex + 2;
                if (modesIndex < 0 || replacementFrameIndex >= capturedWrites.Length)
                {
                    return false;
                }

                var replacementOutput = string.Concat(capturedWrites.Skip(replacementFrameIndex));
                return replacementOutput.StartsWith(synchronizedFrameBegin, StringComparison.Ordinal) &&
                    replacementOutput.IndexOf(
                        synchronizedFrameEnd,
                        synchronizedFrameBegin.Length,
                        StringComparison.Ordinal) >= synchronizedFrameBegin.Length;
            },
            TimeSpan.FromSeconds(5),
            "Clean repaint writes one complete synchronized replacement frame");

        var writes = presentation.CaptureWrites()
            .Select(static write => System.Text.Encoding.UTF8.GetString(write.Span))
            .ToArray();
        var cleanScreenModesIndex = Array.FindIndex(
            writes,
            write => write.Equals(cleanScreenModes, StringComparison.Ordinal));
        Assert.IsGreaterThanOrEqualTo(
            0,
            cleanScreenModesIndex,
            "The clean repaint must end synchronized mode before clearing the Windows screen buffer.");
        Assert.AreEqual(cleanScreenModes, writeBeforeNativeClear);
        Assert.AreEqual(1, Volatile.Read(ref nativeClearCount));
        var cleanScreenOverwriteIndex = cleanScreenModesIndex + 1;
        Assert.IsLessThan(writes.Length, cleanScreenOverwriteIndex);
        Assert.StartsWith(cleanScreenOverwriteBegin, writes[cleanScreenOverwriteIndex]);
        Assert.IsTrue(
            writes[cleanScreenOverwriteIndex].Contains(
                $"\x1b[24;1H\x1b[2K{new string(' ', 99)}\x1b[24;100H \x1b[1X\x1b[H",
                StringComparison.Ordinal),
            "The physical overwrite must replace every cell through the terminal's final row and explicitly erase the right edge.");
        Assert.IsFalse(
            writes[cleanScreenOverwriteIndex].Contains(new string(' ', 100), StringComparison.Ordinal),
            "The physical overwrite must not write through the final column and trigger a deferred line wrap.");
        Assert.IsTrue(
            writes[cleanScreenModesIndex].Contains("\x1b[?7l\x1b[?25l\x1b[0m", StringComparison.Ordinal),
            "The physical clear must disable wrapping and hide the cursor before replacing cells.");
        Assert.IsFalse(
            writes[cleanScreenOverwriteIndex].Contains("\x1b[2J", StringComparison.Ordinal),
            "The replacement must not rely on Windows Terminal honoring an erase-display command.");
        var cleanFrameIndex = cleanScreenOverwriteIndex + 1;
        Assert.IsLessThan(writes.Length, cleanFrameIndex);
        Assert.IsTrue(
            writes[cleanFrameIndex].StartsWith(synchronizedFrameBegin, StringComparison.Ordinal),
            "The replacement frame must start only after the physical screen is blank.");
        var cleanFrameOutput = string.Concat(writes.Skip(cleanFrameIndex));
        var cleanFrameBeginOffset = cleanFrameOutput.IndexOf(
            synchronizedFrameBegin,
            StringComparison.Ordinal);
        var cleanFrameEndOffset = cleanFrameOutput.IndexOf(
            synchronizedFrameEnd,
            cleanFrameBeginOffset + synchronizedFrameBegin.Length,
            StringComparison.Ordinal);
        Assert.AreEqual(
            0,
            cleanFrameBeginOffset,
            "The clean replacement output must begin with its synchronized frame marker.");
        Assert.IsGreaterThan(
            cleanFrameBeginOffset,
            cleanFrameEndOffset,
            "The clean replacement frame must have a synchronized end marker.");
        Assert.DoesNotContain(
            synchronizedFrameBegin,
            cleanFrameOutput.AsSpan(
                cleanFrameBeginOffset + synchronizedFrameBegin.Length,
                cleanFrameEndOffset - cleanFrameBeginOffset - synchronizedFrameBegin.Length).ToString(),
            "The clear and replacement must share one synchronized frame instead of nesting two frames.");
        await automator.Ctrl().KeyAsync(Hex1bKey.Q, timeout.Token);
        await runTask.WaitAsync(timeout.Token);

        using var restored = automator.CreateSnapshot();
        Assert.IsFalse(restored.InAlternateScreen);
        Assert.IsFalse(restored.ContainsText(oldSuffix));
    }
}
