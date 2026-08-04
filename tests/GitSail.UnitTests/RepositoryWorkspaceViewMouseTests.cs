using GitSail.CommandLine;
using GitSail.Ui;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies first-class pointer interaction against the real headless workspace widget tree.
/// </summary>
[TestClass]
public sealed class RepositoryWorkspaceViewMouseTests
{
    /// <summary>
    /// Verifies clicks, modifier selection, wheel input, splitter drag, and action activation.
    /// </summary>
    [TestMethod]
    public async Task Workspace_WithMouseInput_UpdatesControlledStateAndInvokesActions()
    {
        var entries = Enumerable.Range(0, 20)
            .Select(static index => FakeRepositoryWorkspaceSession.CreateUnstagedEntry($"file-{index:00}.txt"))
            .Concat(Enumerable.Range(0, 3)
                .Select(static index => FakeRepositoryWorkspaceSession.CreateStagedEntry($"staged-{index:00}.txt")))
            .ToArray();
        var session = new FakeRepositoryWorkspaceSession(entries);
        var view = new RepositoryWorkspaceView(ApplicationMode.Gui, session, CancellationToken.None);
        Hex1bApp? application = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(120, 30)
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
        var automator = new Hex1bTerminalAutomator(terminal, TimeSpan.FromSeconds(3));

        try
        {
            await automator.WaitUntilTextAsync("Unstaged (20)", TimeSpan.FromSeconds(3));
            await automator.ClickAtAsync(10, 3, MouseButton.Left, timeout.Token);
            await automator.WaitUntilTextAsync("Unstaged: file-01.txt", TimeSpan.FromSeconds(3));

            var readOnlyEditor = session.Diff.Editor;
            var originalPatch = readOnlyEditor.Document.GetText();
            await new Hex1bTerminalInputSequenceBuilder()
                .ClickAt(55, 6, MouseButton.Left)
                .ClickAt(55, 6, MouseButton.Left)
                .Build()
                .ApplyAsync(terminal, timeout.Token);
            await automator.WaitUntilAsync(
                _ => readOnlyEditor.Cursor.HasSelection,
                TimeSpan.FromSeconds(3),
                "Double-click selects a diff word");
            await automator.DragAsync(55, 7, 62, 9, MouseButton.Left, timeout.Token);
            await automator.TypeAsync("xyz", timeout.Token);
            Assert.AreEqual(originalPatch, readOnlyEditor.Document.GetText());
            await automator.KeyAsync(Hex1bKey.S, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.StageFocusedHunkCallCount == 1,
                TimeSpan.FromSeconds(3),
                "S in the diff stages the exact focused hunk");
            Assert.AreEqual(0, session.StageCallCount);
            await automator.KeyAsync(Hex1bKey.J, timeout.Token);
            await automator.KeyAsync(Hex1bKey.K, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.FocusNextHunkCallCount == 1 && session.FocusPreviousHunkCallCount == 1,
                TimeSpan.FromSeconds(3),
                "J and K in the diff dispatch hunk navigation");
            await automator.KeyAsync(Hex1bKey.A, timeout.Token);
            await new Hex1bTerminalInputSequenceBuilder()
                .Shift()
                .Key(Hex1bKey.U)
                .Build()
                .ApplyAsync(terminal, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.StageAllCallCount == 1 && session.UnstageAllCallCount == 1,
                TimeSpan.FromSeconds(3),
                "A and Shift+U dispatch complete index actions from the diff");
            await automator.KeyAsync(Hex1bKey.Oem4, timeout.Token);
            await automator.KeyAsync(Hex1bKey.Oem6, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.DecreaseDiffContextCallCount == 1 && session.IncreaseDiffContextCallCount == 1,
                TimeSpan.FromSeconds(3),
                "Left and right bracket dispatch diff context changes");
            await automator.MouseMoveToAsync(80, 15, timeout.Token);
            await automator.ScrollDownAsync(12, timeout.Token);
            await automator.WaitUntilTextAsync("new line 28", TimeSpan.FromSeconds(3));

            await new Hex1bTerminalInputSequenceBuilder()
                .Ctrl()
                .ClickAt(3, 2, MouseButton.Left)
                .Build()
                .ApplyAsync(terminal, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.State.UnstagedSelectedIndices.Count > 0,
                TimeSpan.FromSeconds(3),
                "Ctrl-click checks a row");

            await new Hex1bTerminalInputSequenceBuilder()
                .Shift()
                .ClickAt(3, 5, MouseButton.Left)
                .Build()
                .ApplyAsync(terminal, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.State.UnstagedSelectedIndices.Count > 1,
                TimeSpan.FromSeconds(3),
                "Shift-click extends a checked range");

            await automator.MouseMoveToAsync(10, 6, timeout.Token);
            await automator.ScrollDownAsync(8, timeout.Token);
            await automator.WaitUntilTextAsync("file-10.txt", TimeSpan.FromSeconds(3));

            await new Hex1bTerminalInputSequenceBuilder()
                .Ctrl()
                .ClickAt(3, 12, MouseButton.Left)
                .Build()
                .ApplyAsync(terminal, timeout.Token);
            await new Hex1bTerminalInputSequenceBuilder()
                .Shift()
                .ClickAt(3, 14, MouseButton.Left)
                .Build()
                .ApplyAsync(terminal, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.State.StagedSelectedIndices.Count == 3,
                TimeSpan.FromSeconds(3),
                "Index pane supports Ctrl-click and Shift-click selection");

            await automator.ClickAtAsync(55, 6, MouseButton.Left, timeout.Token);
            await automator.KeyAsync(Hex1bKey.U, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.UnstageFocusedHunkCallCount == 1,
                TimeSpan.FromSeconds(3),
                "U in the staged diff unstages the exact focused hunk");
            using var stagedSnapshot = automator.CreateSnapshot();
            var stagedActionLine = stagedSnapshot.GetLine(28);
            var unstageHunkX = stagedActionLine.IndexOf("Unstage hunk", StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, unstageHunkX);
            await automator.ClickAtAsync(unstageHunkX + 1, 28, MouseButton.Left, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.UnstageFocusedHunkCallCount == 2,
                TimeSpan.FromSeconds(3),
                "Focused-hunk unstaging is mouse-activatable");

            await automator.DoubleClickAtAsync(10, 4, MouseButton.Left, timeout.Token);
            await automator.DragAsync(20, 10, 20, 14, MouseButton.Left, timeout.Token);
            await automator.ClickAtAsync(3, 28, MouseButton.Left, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.StageCallCount == 1,
                TimeSpan.FromSeconds(3),
                "Stage button is mouse-activatable");
            await automator.ClickAtAsync(12, 28, MouseButton.Left, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.UnstageCallCount == 1,
                TimeSpan.FromSeconds(3),
                "Unstage button is mouse-activatable");
            using var snapshot = automator.CreateSnapshot();
            var actionLine = snapshot.GetLine(28);
            var stageAllX = actionLine.IndexOf("Stage all", StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, stageAllX);
            await automator.ClickAtAsync(stageAllX + 1, 28, MouseButton.Left, timeout.Token);
            var unstageAllX = actionLine.IndexOf("Unstage all", StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, unstageAllX);
            await automator.ClickAtAsync(unstageAllX + 1, 28, MouseButton.Left, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.StageAllCallCount == 2 && session.UnstageAllCallCount == 2,
                TimeSpan.FromSeconds(3),
                "Stage-all and unstage-all actions are mouse-activatable");
            var lessContextX = actionLine.IndexOf("Less context", StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, lessContextX);
            await automator.ClickAtAsync(lessContextX + 1, 28, MouseButton.Left, timeout.Token);
            var moreContextX = actionLine.IndexOf("More context", StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, moreContextX);
            await automator.ClickAtAsync(moreContextX + 1, 28, MouseButton.Left, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.DecreaseDiffContextCallCount == 2 && session.IncreaseDiffContextCallCount == 2,
                TimeSpan.FromSeconds(3),
                "Diff context actions are mouse-activatable");
            var hunkActionX = actionLine.IndexOf("Stage hunk", StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, hunkActionX);
            await automator.ClickAtAsync(hunkActionX + 1, 28, MouseButton.Left, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.StageFocusedHunkCallCount == 2,
                TimeSpan.FromSeconds(3),
                "Focused-hunk staging is mouse-activatable");
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies the minimum-size guard retains a safe mouse-enabled application state.
    /// </summary>
    [TestMethod]
    public async Task Workspace_BelowSupportedMinimum_ShowsResizeGuardWithMouseEnabled()
    {
        var session = new FakeRepositoryWorkspaceSession(
            FakeRepositoryWorkspaceSession.CreateUnstagedEntry("file.txt"));
        var view = new RepositoryWorkspaceView(ApplicationMode.Gui, session, CancellationToken.None);
        Hex1bApp? application = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(59, 17)
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
        var automator = new Hex1bTerminalAutomator(terminal, TimeSpan.FromSeconds(3));

        try
        {
            await automator.WaitUntilTextAsync("Terminal too small", TimeSpan.FromSeconds(3));
            using var snapshot = automator.CreateSnapshot();
            var actionLine = snapshot.GetLine(15);
            var refreshActionX = actionLine.IndexOf("Refresh", StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, refreshActionX);
            await automator.ClickAtAsync(refreshActionX + 1, 15, MouseButton.Left, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.RefreshCallCount == 1,
                TimeSpan.FromSeconds(3),
                "Refresh remains mouse-activatable below the supported minimum");
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }
}
