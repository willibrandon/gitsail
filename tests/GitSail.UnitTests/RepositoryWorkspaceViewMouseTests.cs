using GitSail.CommandLine;
using GitSail.Domain;
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
        var view = new RepositoryWorkspaceView(
            new GitSailShellOptions(ApplicationMode.Gui, WorkingDirectory: null),
            session,
            CancellationToken.None);
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
            await automator.ClickAtAsync(70, 18, MouseButton.Left, timeout.Token);
            await automator.TypeAsync("commit message", timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.CommitMessage.Message.Contains("commit message", StringComparison.Ordinal),
                TimeSpan.FromSeconds(3),
                "The lifted commit editor accepts ordinary text input");
            await automator.KeyAsync(Hex1bKey.F4, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.CommitCallCount == 1 && session.CommitMessage.Message.Length == 0,
                TimeSpan.FromSeconds(3),
                "F4 commits and clears a successful draft");
            await automator.MouseMoveToAsync(80, 10, timeout.Token);
            await automator.ScrollDownAsync(12, timeout.Token);
            await automator.WaitUntilTextAsync("new line 38", TimeSpan.FromSeconds(3));

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
            using var actionsBeforeClick = automator.CreateSnapshot();
            var actionsBeforeClickLine = actionsBeforeClick.GetLine(28);
            var stageX = actionsBeforeClickLine.IndexOf("Stage", StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, stageX);
            await automator.ClickAtAsync(stageX + 1, 28, MouseButton.Left, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.StageCallCount == 1,
                TimeSpan.FromSeconds(3),
                "Stage button is mouse-activatable");
            var unstageX = actionsBeforeClickLine.IndexOf("Unstage", StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, unstageX);
            await automator.ClickAtAsync(unstageX + 1, 28, MouseButton.Left, timeout.Token);
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
            await automator.ClickAtAsync(70, 18, MouseButton.Left, timeout.Token);
            await automator.TypeAsync("mouse commit", timeout.Token);
            var commitX = actionLine.IndexOf("Commit", StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, commitX);
            await automator.ClickAtAsync(commitX + 1, 28, MouseButton.Left, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.CommitCallCount == 2 && session.CommitMessage.Message.Length == 0,
                TimeSpan.FromSeconds(3),
                "Commit is mouse-activatable and clears a successful draft");
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
        var view = new RepositoryWorkspaceView(
            new GitSailShellOptions(ApplicationMode.Gui, WorkingDirectory: null),
            session,
            CancellationToken.None);
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

    /// <summary>
    /// Verifies no-commit citool exposes a pointer-activatable Done action and stops only after validation.
    /// </summary>
    [TestMethod]
    public async Task CitoolNoCommit_WithDoneClick_CompletesPreparedIndexAndStops()
    {
        var session = new FakeRepositoryWorkspaceSession(
            FakeRepositoryWorkspaceSession.CreateUnstagedEntry("prepared.txt"));
        var options = new GitSailShellOptions(
            ApplicationMode.Citool,
            WorkingDirectory: null,
            new CitoolOptions(Amend: false, NoCommit: true, OpenCommitMessage: false));
        var view = new RepositoryWorkspaceView(options, session, CancellationToken.None);
        Hex1bApp? application = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(120, 30)
            .WithHex1bApp(
                terminalOptions => terminalOptions.EnableMouse = true,
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
            await automator.WaitUntilTextAsync("F4 Done", TimeSpan.FromSeconds(3));
            using var snapshot = automator.CreateSnapshot();
            var actionLine = snapshot.GetLine(28);
            var doneX = actionLine.IndexOf("Done", StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, doneX);

            await automator.ClickAtAsync(doneX + 1, 28, MouseButton.Left, timeout.Token);
            await runTask.WaitAsync(timeout.Token);

            Assert.IsTrue(session.IsCitoolCompleted);
            Assert.AreEqual(0, session.CommitCallCount);
            Assert.AreEqual("Index preparation completed", session.Activity);
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies every visible commit option is pointer-activatable and identity fields retain typed input.
    /// </summary>
    [TestMethod]
    public async Task CommitOptions_WithMouseInput_UpdatesCompleteTransactionState()
    {
        var session = new FakeRepositoryWorkspaceSession(
            FakeRepositoryWorkspaceSession.CreateStagedEntry("staged.txt"));
        var view = new RepositoryWorkspaceView(
            new GitSailShellOptions(ApplicationMode.Gui, WorkingDirectory: null),
            session,
            CancellationToken.None);
        Hex1bApp? application = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(120, 30)
            .WithHex1bApp(
                terminalOptions => terminalOptions.EnableMouse = true,
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
            await automator.WaitUntilTextAsync("default transaction", TimeSpan.FromSeconds(3));
            using (var collapsed = automator.CreateSnapshot())
            {
                var optionsPosition = FindText(collapsed, "Options");
                await automator.ClickAtAsync(
                    optionsPosition.X + 1,
                    optionsPosition.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilTextAsync("Cleanup: default", TimeSpan.FromSeconds(3));
            using (var expanded = automator.CreateSnapshot())
            {
                var amendPosition = FindText(expanded, "Amend");
                var signoffPosition = FindText(expanded, "Signoff");
                var cleanupPosition = FindText(expanded, "Cleanup:");
                await automator.ClickAtAsync(
                    amendPosition.X + 1,
                    amendPosition.Y,
                    MouseButton.Left,
                    timeout.Token);
                await automator.ClickAtAsync(
                    signoffPosition.X + 1,
                    signoffPosition.Y,
                    MouseButton.Left,
                    timeout.Token);
                await automator.ClickAtAsync(
                    cleanupPosition.X + 1,
                    cleanupPosition.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilAsync(
                _ => session.CommitOptions.Amend &&
                    session.CommitOptions.Signoff &&
                    session.CommitOptions.CleanupMode == CommitCleanupMode.Strip,
                TimeSpan.FromSeconds(3),
                "Amend, signoff, and cleanup controls update lifted options");
            using (var beforeSigning = automator.CreateSnapshot())
            {
                var signPosition = FindText(beforeSigning, "Sign [");
                await automator.ClickAtAsync(
                    signPosition.X + 1,
                    signPosition.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilTextAsync("Signing key:", TimeSpan.FromSeconds(3));
            using (var identity = automator.CreateSnapshot())
            {
                var authorPosition = FindText(identity, "Author:");
                var signingKeyPosition = FindText(identity, "Signing key:");
                await automator.ClickAtAsync(
                    authorPosition.X + "Author: ".Length,
                    authorPosition.Y,
                    MouseButton.Left,
                    timeout.Token);
                await automator.TypeAsync("A U Thor <author@example.invalid>", timeout.Token);
                await automator.ClickAtAsync(
                    signingKeyPosition.X + "Signing key: ".Length,
                    signingKeyPosition.Y,
                    MouseButton.Left,
                    timeout.Token);
                await automator.TypeAsync("key-id", timeout.Token);
            }

            await automator.WaitUntilAsync(
                _ => session.CommitOptions.SignCommit &&
                    session.CommitOptions.Author.Text == "A U Thor <author@example.invalid>" &&
                    session.CommitOptions.SigningKey.Text == "key-id",
                TimeSpan.FromSeconds(3),
                "Author and signing-key fields retain pointer-focused text input");
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies hook bypass is a separate cancel-first confirmation with a pointer-activatable approval.
    /// </summary>
    [TestMethod]
    public async Task CommitWithoutHooks_WithConfirmation_RequiresExplicitApproval()
    {
        var session = new FakeRepositoryWorkspaceSession(
            FakeRepositoryWorkspaceSession.CreateStagedEntry("staged.txt"));
        var view = new RepositoryWorkspaceView(
            new GitSailShellOptions(ApplicationMode.Gui, WorkingDirectory: null),
            session,
            CancellationToken.None);
        Hex1bApp? application = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(120, 30)
            .WithHex1bApp(
                terminalOptions => terminalOptions.EnableMouse = true,
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
            await automator.WaitUntilTextAsync("default transaction", TimeSpan.FromSeconds(3));
            using (var collapsed = automator.CreateSnapshot())
            {
                var optionsPosition = FindText(collapsed, "Options");
                await automator.ClickAtAsync(
                    optionsPosition.X + 1,
                    optionsPosition.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilTextAsync("Without hooks...", TimeSpan.FromSeconds(3));
            await OpenCommitWithoutHooksConfirmationAsync(automator, timeout.Token);
            await automator.WaitUntilTextAsync("Prepare and post hooks still run.", TimeSpan.FromSeconds(3));
            await automator.KeyAsync(Hex1bKey.Enter, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Prepare and post hooks still run."),
                TimeSpan.FromSeconds(3),
                "The first focused confirmation action closes the modal");
            Assert.AreEqual(0, session.CommitWithoutHooksCallCount);

            await OpenCommitWithoutHooksConfirmationAsync(automator, timeout.Token);
            await automator.WaitUntilTextAsync("Prepare and post hooks still run.", TimeSpan.FromSeconds(3));
            using (var confirmation = automator.CreateSnapshot())
            {
                var approvalPosition = FindTextOnLineWith(
                    confirmation,
                    "Commit without hooks",
                    "Cancel");
                await automator.ClickAtAsync(
                    approvalPosition.X + 1,
                    approvalPosition.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilAsync(
                _ => session.CommitWithoutHooksCallCount == 1,
                TimeSpan.FromSeconds(3),
                "Explicit pointer approval dispatches the separate hook-bypass transaction");
            Assert.AreEqual(0, session.CommitCallCount);
            Assert.AreEqual("Commit completed without bypassable hooks", session.Activity);
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies commit-message citool starts in the editor and exits immediately after its one commit.
    /// </summary>
    [TestMethod]
    public async Task CitoolCommitMessage_WithKeyboardInput_FocusesEditorAndStopsAfterCommit()
    {
        var session = new FakeRepositoryWorkspaceSession(
            FakeRepositoryWorkspaceSession.CreateStagedEntry("staged.txt"));
        var options = new GitSailShellOptions(
            ApplicationMode.Citool,
            WorkingDirectory: null,
            new CitoolOptions(Amend: false, NoCommit: false, OpenCommitMessage: true));
        var view = new RepositoryWorkspaceView(options, session, CancellationToken.None);
        Hex1bApp? application = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(120, 30)
            .WithHex1bApp(
                terminalOptions => terminalOptions.EnableMouse = true,
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
            await automator.WaitUntilTextAsync("Commit message", TimeSpan.FromSeconds(3));
            await automator.TypeAsync("focused citool message", timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.CommitMessage.Message == "focused citool message",
                TimeSpan.FromSeconds(3),
                "The commit-message option places initial focus in the editor");
            await automator.KeyAsync(Hex1bKey.F4, timeout.Token);
            await runTask.WaitAsync(timeout.Token);

            Assert.IsTrue(session.IsCitoolCompleted);
            Assert.AreEqual(1, session.CommitCallCount);
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

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

        Assert.Fail($"Expected terminal text '{text}' was not present.");
        return (-1, -1);
    }

    private static (int X, int Y) FindTextOnLineWith(
        Hex1bTerminalSnapshot snapshot,
        string text,
        string companionText)
    {
        for (var row = 0; row < snapshot.Height; row++)
        {
            var line = snapshot.GetLine(row);
            if (line.Contains(companionText, StringComparison.Ordinal))
            {
                var column = line.IndexOf(text, StringComparison.Ordinal);
                if (column >= 0)
                {
                    return (column, row);
                }
            }
        }

        Assert.Fail($"Expected terminal text '{text}' beside '{companionText}' was not present.");
        return (-1, -1);
    }

    private static async Task OpenCommitWithoutHooksConfirmationAsync(
        Hex1bTerminalAutomator automator,
        CancellationToken cancellationToken)
    {
        using var snapshot = automator.CreateSnapshot();
        var actionPosition = FindText(snapshot, "Without hooks...");
        await automator.ClickAtAsync(
            actionPosition.X + 1,
            actionPosition.Y,
            MouseButton.Left,
            cancellationToken);
    }
}
