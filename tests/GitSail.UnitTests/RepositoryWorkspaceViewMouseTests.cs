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
            await automator.ClickAtAsync(32, 15, MouseButton.Left, timeout.Token);
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
