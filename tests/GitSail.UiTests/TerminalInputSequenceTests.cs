using GitSail.Ui;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;

namespace GitSail.UiTests;

/// <summary>
/// Verifies raw baseline terminal sequences reach their exact application bindings.
/// Covers every supported read boundary instead of relying on synthesized key events.
/// </summary>
[TestClass]
public sealed class TerminalInputSequenceTests
{
    /// <summary>
    /// Verifies every ASCII control byte reaches its exact baseline key interpretation.
    /// Proves aliases are decoded once and no control byte becomes visible text.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_WithEveryAsciiControl_DeliversExactKeyOnce()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var presentation = new DelayedPresentationAdapter(
            80,
            24,
            TimeSpan.FromMilliseconds(1));
        var counts = new int[32];
        await using var session = new TerminalApplicationSession(
            context => context.Text("ASCII control audit").InputBindings(bindings =>
            {
                bindings.Ctrl().Key(Hex1bKey.Spacebar).Action(
                    () => Interlocked.Increment(ref counts[0]),
                    "Count NUL");
                for (var value = 1; value <= 26; value++)
                {
                    if (value is 8 or 9 or 10 or 13)
                    {
                        continue;
                    }

                    var controlValue = value;
                    bindings.Ctrl().Key(Hex1bKey.A + (value - 1)).Action(
                        () => Interlocked.Increment(ref counts[controlValue]),
                        $"Count control byte {value}");
                }

                bindings.Key(Hex1bKey.Backspace).Action(
                    () => Interlocked.Increment(ref counts[8]),
                    "Count Backspace or Delete");
                bindings.Key(Hex1bKey.Tab).Action(
                    () => Interlocked.Increment(ref counts[9]),
                    "Count Tab");
                bindings.Key(Hex1bKey.Enter).Action(
                    () => Interlocked.Increment(ref counts[13]),
                    "Count Enter");
                bindings.Key(Hex1bKey.Escape).Action(
                    () => Interlocked.Increment(ref counts[27]),
                    "Count Escape");
            }).Fill(),
            new Hex1bAppOptions
            {
                EnableMouse = true,
                EnableDefaultCtrlCExit = false,
            },
            presentation,
            discardBareMouseReports: true);
        var automator = new Hex1bTerminalAutomator(session.Terminal, TimeSpan.FromSeconds(5));
        var runTask = session.RunAsync(timeout.Token);

        await automator.WaitUntilTextAsync("ASCII control audit", TimeSpan.FromSeconds(5));
        for (var value = 0; value <= 31; value++)
        {
            await presentation.SendInputAsync(new byte[] { (byte)value }, timeout.Token);
            if (value >= 28)
            {
                continue;
            }

            var observedIndex = value == 10 ? 13 : value;
            var expectedCount = value == 13 ? 2 : 1;
            await automator.WaitUntilAsync(
                _ => Volatile.Read(ref counts[observedIndex]) == expectedCount,
                TimeSpan.FromSeconds(5),
                $"ASCII control byte {value} invokes its exact action once");
        }

        await presentation.SendInputAsync(new byte[] { 0x7F }, timeout.Token);
        await automator.WaitUntilAsync(
            _ => Volatile.Read(ref counts[8]) == 2,
            TimeSpan.FromSeconds(5),
            "ASCII Delete invokes the Backspace alias exactly once");

        session.Application.RequestStop();
        await runTask.WaitAsync(timeout.Token);
        for (var index = 0; index < counts.Length; index++)
        {
            Assert.AreEqual(
                index switch
                {
                    8 or 13 => 2,
                    10 => 0,
                    >= 28 => 0,
                    _ => 1,
                },
                Volatile.Read(ref counts[index]),
                $"Unexpected count at index {index}.");
        }
    }

    /// <summary>
    /// Verifies navigation, editing, and function-key sequences survive every split boundary.
    /// Prevents fragmented terminal input from becoming text or invoking a different action.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_WithEverySplitEscapeSequence_DeliversExactKeyOnce()
    {
        var sequences = new (Hex1bKey Key, string Sequence)[]
        {
            (Hex1bKey.UpArrow, "\u001b[A"),
            (Hex1bKey.DownArrow, "\u001b[B"),
            (Hex1bKey.RightArrow, "\u001b[C"),
            (Hex1bKey.LeftArrow, "\u001b[D"),
            (Hex1bKey.Home, "\u001b[H"),
            (Hex1bKey.End, "\u001b[F"),
            (Hex1bKey.Insert, "\u001b[2~"),
            (Hex1bKey.Delete, "\u001b[3~"),
            (Hex1bKey.PageUp, "\u001b[5~"),
            (Hex1bKey.PageDown, "\u001b[6~"),
            (Hex1bKey.F1, "\u001bOP"),
            (Hex1bKey.F2, "\u001bOQ"),
            (Hex1bKey.F3, "\u001bOR"),
            (Hex1bKey.F4, "\u001bOS"),
            (Hex1bKey.F5, "\u001b[15~"),
            (Hex1bKey.F6, "\u001b[17~"),
            (Hex1bKey.F7, "\u001b[18~"),
            (Hex1bKey.F8, "\u001b[19~"),
            (Hex1bKey.F9, "\u001b[20~"),
            (Hex1bKey.F10, "\u001b[21~"),
            (Hex1bKey.F11, "\u001b[23~"),
            (Hex1bKey.F12, "\u001b[24~"),
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var presentation = new DelayedPresentationAdapter(
            80,
            24,
            TimeSpan.FromMilliseconds(1));
        var counts = new int[sequences.Length];
        await using var session = new TerminalApplicationSession(
            context => context.Text("Raw terminal sequence audit").InputBindings(bindings =>
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
                EnableDefaultCtrlCExit = false,
            },
            presentation,
            discardBareMouseReports: true);
        var automator = new Hex1bTerminalAutomator(session.Terminal, TimeSpan.FromSeconds(5));
        var runTask = session.RunAsync(timeout.Token);

        await automator.WaitUntilTextAsync("Raw terminal sequence audit", TimeSpan.FromSeconds(5));
        for (var sequenceIndex = 0; sequenceIndex < sequences.Length; sequenceIndex++)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(sequences[sequenceIndex].Sequence);
            for (var split = 1; split < bytes.Length; split++)
            {
                await presentation.SendInputAsync(bytes.AsMemory(0, split), timeout.Token);
                await presentation.SendInputAsync(bytes.AsMemory(split), timeout.Token);
                var expectedCount = split;
                var observedIndex = sequenceIndex;
                await automator.WaitUntilAsync(
                    _ => Volatile.Read(ref counts[observedIndex]) == expectedCount,
                    TimeSpan.FromSeconds(5),
                    $"{sequences[sequenceIndex].Key} split at byte {split} invokes its exact action once");
            }
        }

        await automator.Ctrl().KeyAsync(Hex1bKey.Q, timeout.Token);
        await runTask.WaitAsync(timeout.Token);
        for (var index = 0; index < sequences.Length; index++)
        {
            Assert.AreEqual(
                sequences[index].Sequence.Length - 1,
                Volatile.Read(ref counts[index]),
                $"Unexpected invocation count for {sequences[index].Key}.");
        }
    }
}
