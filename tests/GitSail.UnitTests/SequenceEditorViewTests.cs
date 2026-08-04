using GitSail.Domain;
using GitSail.Ui;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies responsive keyboard-and-mouse behavior in the typed rebase todo editor.
/// </summary>
[TestClass]
public sealed class SequenceEditorViewTests
{
    /// <summary>
    /// Verifies the complete compact editor remains readable at the supported 60-by-18 minimum.
    /// </summary>
    [TestMethod]
    public async Task Build_AtMinimumSize_ShowsPlanActionsAndCompleteGitVersion()
    {
        var session = CreateSession();
        var view = new SequenceEditorView(session, "sample-repository", "2.51.1.windows.1");
        Hex1bApp? application = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current!.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(60, 18)
            .WithHex1bApp(
                options => options.EnableMouse = true,
                createdApplication =>
                {
                    application = createdApplication;
                    view.Attach(createdApplication);
                    return view.Build;
                })
            .Build();
        var runTask = terminal.RunAsync(timeout.Token);
        var automator = new Hex1bTerminalAutomator(terminal, TimeSpan.FromSeconds(5));

        try
        {
            await automator.WaitUntilTextAsync("Git 2.51.1.windows.1", TimeSpan.FromSeconds(5));
            using var snapshot = automator.CreateSnapshot();
            Assert.IsTrue(snapshot.ContainsText("Rebase plan"));
            Assert.IsTrue(snapshot.ContainsText("Save plan..."));
            Assert.IsTrue(snapshot.ContainsText("Ctrl+S Save"));
            Assert.IsTrue(snapshot.ContainsText("Esc Cancel"));
            Assert.IsFalse(snapshot.ContainsText("More room needed"));
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies pointer click-away and Escape both dismiss the exec dialog without editing the plan.
    /// </summary>
    [TestMethod]
    public async Task AddExecDialog_WithClickAwayAndEscape_DismissesWithoutInsertion()
    {
        var session = CreateSession();
        var view = new SequenceEditorView(session, "sample-repository", "2.51.1");
        Hex1bApp? application = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current!.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(100, 26)
            .WithHex1bApp(
                options => options.EnableMouse = true,
                createdApplication =>
                {
                    application = createdApplication;
                    view.Attach(createdApplication);
                    return view.Build;
                })
            .Build();
        var runTask = terminal.RunAsync(timeout.Token);
        var automator = new Hex1bTerminalAutomator(terminal, TimeSpan.FromSeconds(5));

        try
        {
            await automator.WaitUntilTextAsync("Add exec...", TimeSpan.FromSeconds(5));
            using (var initial = automator.CreateSnapshot())
            {
                var add = FindText(initial, "Add exec...");
                await automator.ClickAtAsync(add.X + 1, add.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Add shell command?", TimeSpan.FromSeconds(5));
            await automator.ClickAtAsync(0, 1, MouseButton.Left, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Add shell command?"),
                TimeSpan.FromSeconds(5),
                "Pointer click-away closes the exec dialog");
            await automator.KeyAsync(Hex1bKey.A, timeout.Token);
            await automator.WaitUntilTextAsync("Add shell command?", TimeSpan.FromSeconds(5));
            await automator.KeyAsync(Hex1bKey.Escape, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Add shell command?"),
                TimeSpan.FromSeconds(5),
                "Escape closes the exec dialog");
            Assert.IsFalse(session.HasExecCommands);
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies pointer selection and action buttons edit the exact selected commit row.
    /// </summary>
    [TestMethod]
    public async Task Plan_WithPointerSelectionAndDrop_ChangesSelectedCommitOnly()
    {
        var session = CreateSession();
        var view = new SequenceEditorView(session, "sample-repository", "2.51.1");
        Hex1bApp? application = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current!.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(100, 26)
            .WithHex1bApp(
                options => options.EnableMouse = true,
                createdApplication =>
                {
                    application = createdApplication;
                    view.Attach(createdApplication);
                    return view.Build;
                })
            .Build();
        var runTask = terminal.RunAsync(timeout.Token);
        var automator = new Hex1bTerminalAutomator(terminal, TimeSpan.FromSeconds(5));

        try
        {
            await automator.WaitUntilTextAsync("pick 2222 two", TimeSpan.FromSeconds(5));
            using (var initial = automator.CreateSnapshot())
            {
                var second = FindText(initial, "pick 2222 two");
                await automator.ClickAtAsync(second.X + 2, second.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilAsync(
                _ => session.FocusedEntry?.DisplayText == "pick 2222 two",
                TimeSpan.FromSeconds(5),
                "Pointer selection focuses the second todo row");
            using (var focused = automator.CreateSnapshot())
            {
                var drop = FindText(focused, "Drop");
                await automator.ClickAtAsync(drop.X + 1, drop.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilAsync(
                snapshot => session.FocusedEntry?.Action == RebaseTodoAction.Drop &&
                    snapshot.ContainsText("drop 2222 two"),
                TimeSpan.FromSeconds(5),
                "Drop changes only the pointer-selected commit row");
            Assert.AreEqual("pick 1111 one", session.Document.Entries[0].DisplayText);
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    private static SequenceEditorSession CreateSession()
        => new(RebaseTodoParser.Parse(
            "pick 1111 one\n# keep this comment\npick 2222 two\n"u8));

    private static (int X, int Y) FindText(Hex1bTerminalSnapshot snapshot, string text)
    {
        for (var row = 0; row < snapshot.Height; row++)
        {
            var column = snapshot.GetLine(row).IndexOf(text, StringComparison.Ordinal);
            if (column >= 0)
            {
                return (column, row);
            }
        }

        Assert.Fail($"Text '{text}' was not found in the terminal snapshot.");
        return (-1, -1);
    }
}
