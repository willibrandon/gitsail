using GitSail.Ui;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;
using Hex1b.Widgets;

namespace GitSail.UiTests;

/// <summary>
/// Verifies full-screen terminal sessions restore the calling shell after exit.
/// </summary>
[TestClass]
public sealed class TerminalApplicationSessionTests
{
    /// <summary>
    /// Verifies a configured ASCII policy is applied by the complete terminal session boundary.
    /// Replaces application and border glyphs before they reach the physical presentation.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_WithAsciiTextPolicy_PresentsOnlyAsciiGlyphs()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var presentation = new DelayedPresentationAdapter(
            80,
            24,
            TimeSpan.FromMilliseconds(10));
        await using var session = new TerminalApplicationSession(
            context => context.Border(context.Text("arrow → CJK 漢"))
                .Title("Unicode │ policy")
                .InputBindings(bindings => bindings.Ctrl().Key(Hex1bKey.Q).Action(
                    actionContext => actionContext.RequestStop(),
                    "Quit test application"))
                .Fill(),
            new Hex1bAppOptions
            {
                EnableMouse = true,
                EnableDefaultCtrlCExit = true,
            },
            presentation,
            textPolicyProvider: static () => new TerminalTextPolicy(
                UseAscii: true,
                AmbiguousWidth: 1));
        var automator = new Hex1bTerminalAutomator(session.Terminal, TimeSpan.FromSeconds(5));
        var runTask = session.RunAsync(timeout.Token);

        await automator.WaitUntilTextAsync("arrow → CJK 漢", TimeSpan.FromSeconds(5));
        await automator.WaitUntilAsync(
            _ => string.Concat(presentation.CaptureWrites().Select(static write =>
                    System.Text.Encoding.UTF8.GetString(write.Span)))
                .Contains("arrow > CJK ??", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5),
            "The physical output contains the width-preserving ASCII presentation");
        var physicalOutput = string.Concat(presentation.CaptureWrites().Select(static write =>
            System.Text.Encoding.UTF8.GetString(write.Span)));
        using (var snapshot = automator.CreateSnapshot())
        {
            Assert.IsTrue(snapshot.ContainsText("Unicode │ policy"));
            Assert.IsTrue(snapshot.ContainsText("arrow → CJK 漢"));
        }

        StringAssert.Contains(physicalOutput, "Unicode | policy");
        Assert.DoesNotContain("→", physicalOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("漢", physicalOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("│", physicalOutput, StringComparison.Ordinal);

        await automator.Ctrl().KeyAsync(Hex1bKey.Q, timeout.Token);
        await runTask.WaitAsync(timeout.Token);
    }

    /// <summary>
    /// Verifies malformed mouse reports cannot reach a focused text box in a full terminal session.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_WithBareMouseReports_DiscardsReportsBeforeFocusedTextInput()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var presentation = new DelayedPresentationAdapter(
            80,
            24,
            TimeSpan.FromMilliseconds(10));
        var text = new TextBoxState();
        await using var session = new TerminalApplicationSession(
            context => context.VStack(builder =>
            [
                builder.TextBox().State(text),
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
            discardBareMouseReports: true);
        var automator = new Hex1bTerminalAutomator(session.Terminal, TimeSpan.FromSeconds(5));
        var runTask = session.RunAsync(timeout.Token);

        await automator.WaitUntilTextAsync("Ctrl+Q exits", TimeSpan.FromSeconds(5));
        await presentation.SendInputAsync(
            "[<35;107;13M[<35;83;6Mmain"u8.ToArray(),
            timeout.Token);
        await automator.WaitUntilTextAsync("main", TimeSpan.FromSeconds(5));

        Assert.AreEqual("main", text.Text);
        using (var snapshot = automator.CreateSnapshot())
        {
            Assert.IsFalse(snapshot.ContainsText("[<35;"));
        }

        await automator.Ctrl().KeyAsync(Hex1bKey.Q, timeout.Token);
        await runTask.WaitAsync(timeout.Token);
    }

    /// <summary>
    /// Verifies Windows-style mouse reports split after Escape never become focused text input.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_WithEscapeSplitMouseReports_DiscardsTheirTextTails()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var presentation = new DelayedPresentationAdapter(
            80,
            24,
            TimeSpan.FromMilliseconds(10));
        var text = new TextBoxState();
        await using var session = new TerminalApplicationSession(
            context => context.VStack(builder =>
            [
                builder.TextBox().State(text),
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
            discardBareMouseReports: true);
        var automator = new Hex1bTerminalAutomator(session.Terminal, TimeSpan.FromSeconds(5));
        var runTask = session.RunAsync(timeout.Token);

        await automator.WaitUntilTextAsync("Ctrl+Q exits", TimeSpan.FromSeconds(5));
        await presentation.SendInputAsync("\u001b"u8.ToArray(), timeout.Token);
        await presentation.SendInputAsync("[<35;107;13M"u8.ToArray(), timeout.Token);
        await presentation.SendInputAsync("\u001b"u8.ToArray(), timeout.Token);
        await presentation.SendInputAsync("[<35;83;6Mmain"u8.ToArray(), timeout.Token);
        await automator.WaitUntilTextAsync("main", TimeSpan.FromSeconds(5));

        Assert.AreEqual("main", text.Text);
        using (var snapshot = automator.CreateSnapshot())
        {
            Assert.IsFalse(snapshot.ContainsText("[<35;"));
        }

        await automator.Ctrl().KeyAsync(Hex1bKey.Q, timeout.Token);
        await runTask.WaitAsync(timeout.Token);
    }

    /// <summary>
    /// Verifies a recognized mouse report survives a scheduling gap without becoming text.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_WithSlowFragmentedMouseReport_DiscardsCompleteReport()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var presentation = new DelayedPresentationAdapter(
            80,
            24,
            TimeSpan.FromMilliseconds(25));
        var text = new TextBoxState();
        await using var session = new TerminalApplicationSession(
            context => context.VStack(builder =>
            [
                builder.TextBox().State(text),
                builder.Text("Slow mouse report audit"),
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
            discardBareMouseReports: true);
        var automator = new Hex1bTerminalAutomator(session.Terminal, TimeSpan.FromSeconds(5));
        var runTask = session.RunAsync(timeout.Token);

        await automator.WaitUntilTextAsync("Slow mouse report audit", TimeSpan.FromSeconds(5));
        await presentation.SendInputAsync("[<35;"u8.ToArray(), timeout.Token);
        var writesBeforeGap = presentation.CaptureWrites().Count;
        session.RequestCleanRepaint();
        await automator.WaitUntilAsync(
            _ => presentation.CaptureWrites().Count >= writesBeforeGap + 3,
            TimeSpan.FromSeconds(5),
            "A complete physical repaint creates an observable scheduling gap");
        await presentation.SendInputAsync("107;13Mmain"u8.ToArray(), timeout.Token);
        await automator.WaitUntilTextAsync("main", TimeSpan.FromSeconds(5));

        Assert.AreEqual("main", text.Text);
        using (var snapshot = automator.CreateSnapshot())
        {
            Assert.IsFalse(snapshot.ContainsText("[<35;"));
        }

        await automator.Ctrl().KeyAsync(Hex1bKey.Q, timeout.Token);
        await runTask.WaitAsync(timeout.Token);
    }

    /// <summary>
    /// Verifies the bounded continuation wait still delivers one raw Escape to the application.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_WithStandaloneEscape_DeliversEscapeOnce()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var presentation = new DelayedPresentationAdapter(
            80,
            24,
            TimeSpan.FromMilliseconds(10));
        var escapeCount = 0;
        await using var session = new TerminalApplicationSession(
            context => context.Text("Press Escape").InputBindings(bindings =>
            {
                bindings.Key(Hex1bKey.Escape).Action(
                    () => Interlocked.Increment(ref escapeCount),
                    "Count Escape");
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
            discardBareMouseReports: true);
        var automator = new Hex1bTerminalAutomator(session.Terminal, TimeSpan.FromSeconds(5));
        var runTask = session.RunAsync(timeout.Token);

        await automator.WaitUntilTextAsync("Press Escape", TimeSpan.FromSeconds(5));
        await presentation.SendInputAsync("\u001b"u8.ToArray(), timeout.Token);
        await automator.WaitUntilAsync(
            _ => Volatile.Read(ref escapeCount) == 1,
            TimeSpan.FromSeconds(5),
            "A standalone Escape is delivered exactly once after the bounded continuation wait");

        await automator.Ctrl().KeyAsync(Hex1bKey.Q, timeout.Token);
        await runTask.WaitAsync(timeout.Token);
        Assert.AreEqual(1, Volatile.Read(ref escapeCount));
    }

    /// <summary>
    /// Verifies split baseline function-key sequences reach their exact application bindings once.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_WithSplitBaselineFunctionKeys_DeliversEveryKeyOnce()
    {
        var sequences = new (Hex1bKey Key, string Tail)[]
        {
            (Hex1bKey.F1, "OP"),
            (Hex1bKey.F2, "OQ"),
            (Hex1bKey.F3, "OR"),
            (Hex1bKey.F4, "OS"),
            (Hex1bKey.F5, "[15~"),
            (Hex1bKey.F6, "[17~"),
            (Hex1bKey.F7, "[18~"),
            (Hex1bKey.F8, "[19~"),
            (Hex1bKey.F9, "[20~"),
            (Hex1bKey.F10, "[21~"),
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var presentation = new DelayedPresentationAdapter(
            80,
            24,
            TimeSpan.FromMilliseconds(10));
        var text = new TextBoxState();
        var counts = new int[sequences.Length];
        await using var session = new TerminalApplicationSession(
            context => context.VStack(builder =>
            [
                builder.TextBox().State(text),
                builder.Text("Function key audit"),
            ]).InputBindings(bindings =>
            {
                for (var index = 0; index < sequences.Length; index++)
                {
                    var bindingIndex = index;
                    bindings.Key(sequences[index].Key).Action(
                        () => Interlocked.Increment(ref counts[bindingIndex]),
                        $"Count {sequences[index].Key}");
                }

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
            discardBareMouseReports: true);
        var automator = new Hex1bTerminalAutomator(session.Terminal, TimeSpan.FromSeconds(5));
        var runTask = session.RunAsync(timeout.Token);

        await automator.WaitUntilTextAsync("Function key audit", TimeSpan.FromSeconds(5));
        for (var index = 0; index < sequences.Length; index++)
        {
            await presentation.SendInputAsync("\u001b"u8.ToArray(), timeout.Token);
            await presentation.SendInputAsync(
                System.Text.Encoding.ASCII.GetBytes(sequences[index].Tail),
                timeout.Token);
            var expectedIndex = index;
            await automator.WaitUntilAsync(
                _ => Volatile.Read(ref counts[expectedIndex]) == 1,
                TimeSpan.FromSeconds(5),
                $"Split {sequences[index].Key} reaches its exact application binding");
        }

        Assert.AreEqual(string.Empty, text.Text);
        foreach (var count in counts)
        {
            Assert.AreEqual(1, count);
        }

        await automator.Ctrl().KeyAsync(Hex1bKey.Q, timeout.Token);
        await runTask.WaitAsync(timeout.Token);
    }

    /// <summary>
    /// Verifies standalone context brackets remain exact application input after bounded filtering.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_WithStandaloneContextBrackets_DeliversEachBracketOnce()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var presentation = new DelayedPresentationAdapter(
            80,
            24,
            TimeSpan.FromMilliseconds(10));
        var lessCount = 0;
        var moreCount = 0;
        await using var session = new TerminalApplicationSession(
            context => context.Text("Context key audit").InputBindings(bindings =>
            {
                bindings.Character(static text => text == "[").Action(
                    _ => Interlocked.Increment(ref lessCount),
                    "Decrease context");
                bindings.Character(static text => text == "]").Action(
                    _ => Interlocked.Increment(ref moreCount),
                    "Increase context");
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
            discardBareMouseReports: true);
        var automator = new Hex1bTerminalAutomator(session.Terminal, TimeSpan.FromSeconds(5));
        var runTask = session.RunAsync(timeout.Token);

        await automator.WaitUntilTextAsync("Context key audit", TimeSpan.FromSeconds(5));
        await presentation.SendInputAsync("["u8.ToArray(), timeout.Token);
        await automator.WaitUntilAsync(
            _ => Volatile.Read(ref lessCount) == 1,
            TimeSpan.FromSeconds(5),
            "A standalone left bracket reaches the decrease-context action");
        await presentation.SendInputAsync("]"u8.ToArray(), timeout.Token);
        await automator.WaitUntilAsync(
            _ => Volatile.Read(ref moreCount) == 1,
            TimeSpan.FromSeconds(5),
            "A standalone right bracket reaches the increase-context action");

        await automator.Ctrl().KeyAsync(Hex1bKey.Q, timeout.Token);
        await runTask.WaitAsync(timeout.Token);
        Assert.AreEqual(1, Volatile.Read(ref lessCount));
        Assert.AreEqual(1, Volatile.Read(ref moreCount));
    }

    /// <summary>
    /// Verifies platform input flags are applied after the presentation selects raw mode.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_WithInputModeConfiguration_AppliesItAfterRawModeEntry()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var presentation = new DelayedPresentationAdapter(
            80,
            24,
            TimeSpan.FromMilliseconds(10));
        var configurationCount = 0;
        var rawModeWasActive = false;
        await using var session = new TerminalApplicationSession(
            context => context.Text("Ctrl+Q exits").InputBindings(bindings =>
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
            configureInputMode: () =>
            {
                rawModeWasActive = presentation.IsRawMode;
                Interlocked.Increment(ref configurationCount);
            });
        var automator = new Hex1bTerminalAutomator(session.Terminal, TimeSpan.FromSeconds(5));
        var runTask = session.RunAsync(timeout.Token);

        await automator.WaitUntilTextAsync("Ctrl+Q exits", TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, Volatile.Read(ref configurationCount));
        Assert.IsTrue(rawModeWasActive);

        await automator.Ctrl().KeyAsync(Hex1bKey.Q, timeout.Token);
        await runTask.WaitAsync(timeout.Token);
    }

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
