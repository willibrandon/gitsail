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
    /// Verifies conflict editing, exact side choices, mode selection, and staging support keyboard and mouse.
    /// </summary>
    [TestMethod]
    public async Task ConflictResult_WithKeyboardAndMouseInput_InvokesCompleteResolutionWorkflow()
    {
        var session = new FakeRepositoryWorkspaceSession(
            FakeRepositoryWorkspaceSession.CreateUnstagedEntry("conflict.txt"));
        session.ConfigureConflict(chunkCount: 2);
        var view = new RepositoryWorkspaceView(
            new GitSailShellOptions(ApplicationMode.Gui, WorkingDirectory: null),
            session,
            CancellationToken.None);
        Hex1bApp? application = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(200, 30)
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
            await automator.WaitUntilTextAsync("Use ours", TimeSpan.FromSeconds(3));
            await automator.ClickAtAsync(55, 6, MouseButton.Left, timeout.Token);
            var originalLength = session.Diff.Editor.Document.Length;
            await automator.TypeAsync("sample", timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.Diff.Editor.Document.Length == originalLength + "sample".Length,
                TimeSpan.FromSeconds(3),
                "The active conflict result remains a normally writable editor");
            Assert.AreEqual(0, session.StageCallCount);
            Assert.AreEqual(0, session.StageAllCallCount);

            await new Hex1bTerminalInputSequenceBuilder()
                .Alt()
                .Key(Hex1bKey.O)
                .Build()
                .ApplyAsync(terminal, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.ChooseConflictChunkCallCount == 1 &&
                    session.LastConflictChoice == ConflictResolutionChoice.Ours,
                TimeSpan.FromSeconds(3),
                "Alt+O dispatches the focused ours choice without stealing ordinary typing");

            using (var choices = automator.CreateSnapshot())
            {
                var theirs = FindText(choices, "Use theirs");
                await automator.ClickAtAsync(theirs.X + 1, theirs.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilAsync(
                _ => session.ChooseConflictChunkCallCount == 2 &&
                    session.LastConflictChoice == ConflictResolutionChoice.Theirs &&
                    session.CanStageConflictResolution,
                TimeSpan.FromSeconds(3),
                "The pointer-activated theirs choice completes the fake result");
            using (var completed = automator.CreateSnapshot())
            {
                var mode = FindText(completed, "Mode: regular");
                await automator.ClickAtAsync(mode.X + 1, mode.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilAsync(
                _ => session.ToggleConflictExecutableCallCount == 1 &&
                    session.ConflictResultIsExecutable,
                TimeSpan.FromSeconds(3),
                "The executable result mode is pointer-activatable");
            using (var ready = automator.CreateSnapshot())
            {
                var stage = FindText(ready, "Stage resolution");
                await automator.ClickAtAsync(stage.X + 1, stage.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilAsync(
                _ => session.StageConflictResolutionCallCount == 1,
                TimeSpan.FromSeconds(3),
                "The completed conflict result is pointer-activatable for staging");
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
    /// Verifies published amend is cancel-first, lists every matching ref, and accepts explicit pointer approval.
    /// </summary>
    [TestMethod]
    public async Task PublishedAmend_WithKeyboardAndMouse_RequiresCompleteExplicitWarning()
    {
        var publishedWarning = new PublishedAmendWarning(
        [
            RefName.FromBytes("refs/remotes/origin/main"u8),
            RefName.FromBytes("refs/remotes/upstream/release"u8),
        ]);
        var session = new FakeRepositoryWorkspaceSession(
            FakeRepositoryWorkspaceSession.CreateStagedEntry("staged.txt"))
        {
            PublishedAmendWarning = publishedWarning,
        };
        session.CommitOptions.ToggleAmend();
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
            await automator.WaitUntilTextAsync("Commit", TimeSpan.FromSeconds(3));
            await automator.KeyAsync(Hex1bKey.F4, timeout.Token);
            await automator.WaitUntilTextAsync("Amend published commit?", TimeSpan.FromSeconds(3));
            using (var warning = automator.CreateSnapshot())
            {
                Assert.IsTrue(warning.ContainsText("origin/main"));
                Assert.IsTrue(warning.ContainsText("upstream/release"));
                Assert.IsTrue(warning.ContainsText("local heuristic"));
            }

            await automator.KeyAsync(Hex1bKey.Enter, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Amend published commit?"),
                TimeSpan.FromSeconds(3),
                "The first focused action cancels the published-amend confirmation");
            Assert.AreEqual(0, session.CommitAfterWarningsCallCount);
            Assert.AreEqual(0, session.CommitCallCount);

            await automator.KeyAsync(Hex1bKey.F4, timeout.Token);
            await automator.WaitUntilTextAsync("Amend published commit?", TimeSpan.FromSeconds(3));
            using (var warning = automator.CreateSnapshot())
            {
                var approvalPosition = FindTextOnLineWith(warning, "Amend anyway", "Cancel");
                await automator.ClickAtAsync(
                    approvalPosition.X + 1,
                    approvalPosition.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilAsync(
                _ => session.CommitAfterWarningsCallCount == 1,
                TimeSpan.FromSeconds(3),
                "Pointer approval dispatches only the confirmed published-amend transaction");
            Assert.AreEqual(0, session.CommitCallCount);
            Assert.AreEqual("Confirmed commit completed", session.Activity);
            Assert.AreSame(publishedWarning, session.LastConfirmedPublishedAmendWarning);
            Assert.IsNull(session.LastConfirmedDetachedHeadWarning);

            using (var workspace = automator.CreateSnapshot())
            {
                var optionsPosition = FindText(workspace, "Options");
                await automator.ClickAtAsync(
                    optionsPosition.X + 1,
                    optionsPosition.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilTextAsync("Without hooks...", TimeSpan.FromSeconds(3));
            await OpenCommitWithoutHooksConfirmationAsync(automator, timeout.Token);
            await automator.WaitUntilTextAsync(
                "HEAD is also contained by these local remote-tracking refs:",
                TimeSpan.FromSeconds(3));
            using (var warning = automator.CreateSnapshot())
            {
                Assert.IsTrue(warning.ContainsText("origin/main"));
                Assert.IsTrue(warning.ContainsText("upstream/release"));
                Assert.IsTrue(warning.ContainsText("local heuristic"));
            }

            await automator.KeyAsync(Hex1bKey.Enter, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("HEAD is also contained"),
                TimeSpan.FromSeconds(3),
                "The combined hook-bypass and published-amend warning remains cancel-first");
            Assert.AreEqual(0, session.CommitWithoutHooksCallCount);

            await OpenCommitWithoutHooksConfirmationAsync(automator, timeout.Token);
            await automator.WaitUntilTextAsync("HEAD is also contained", TimeSpan.FromSeconds(3));
            using (var warning = automator.CreateSnapshot())
            {
                var approvalPosition = FindTextOnLineWith(
                    warning,
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
                "Pointer approval dispatches the combined confirmed transaction");
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies detached-HEAD commit confirmation is cancel-first and supports explicit pointer approval.
    /// </summary>
    [TestMethod]
    public async Task DetachedHeadCommit_WithKeyboardAndMouse_RequiresExplicitWarning()
    {
        Assert.IsTrue(ObjectId.TryParseHex(
            "0123456789abcdef0123456789abcdef01234567"u8,
            out var detachedHead));
        Assert.IsTrue(ObjectId.TryParseHex(
            "fedcba9876543210fedcba9876543210fedcba98"u8,
            out var refreshedDetachedHead));
        var detachedWarning = new DetachedHeadWarning(detachedHead!);
        var refreshedDetachedWarning = new DetachedHeadWarning(refreshedDetachedHead!);
        var session = new FakeRepositoryWorkspaceSession(
            FakeRepositoryWorkspaceSession.CreateStagedEntry("staged.txt"))
        {
            DetachedHeadWarning = detachedWarning,
        };
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
            await automator.WaitUntilTextAsync("Commit", TimeSpan.FromSeconds(3));
            await automator.KeyAsync(Hex1bKey.F4, timeout.Token);
            await automator.WaitUntilTextAsync("Commit detached HEAD?", TimeSpan.FromSeconds(3));
            using (var warning = automator.CreateSnapshot())
            {
                Assert.IsTrue(warning.ContainsText("0123456789ab"));
                Assert.IsTrue(warning.ContainsText("will not belong to a branch"));
                Assert.IsTrue(warning.ContainsText("may become unreachable"));
            }

            await automator.KeyAsync(Hex1bKey.Enter, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Commit detached HEAD?"),
                TimeSpan.FromSeconds(3),
                "The first focused action cancels detached-HEAD confirmation");
            Assert.AreEqual(0, session.CommitAfterWarningsCallCount);
            Assert.AreEqual(0, session.CommitCallCount);

            await automator.KeyAsync(Hex1bKey.F4, timeout.Token);
            await automator.WaitUntilTextAsync("Commit detached HEAD?", TimeSpan.FromSeconds(3));
            session.DetachedHeadWarning = refreshedDetachedWarning;
            using (var warning = automator.CreateSnapshot())
            {
                var approvalPosition = FindTextOnLineWith(warning, "Commit anyway", "Cancel");
                await automator.ClickAtAsync(
                    approvalPosition.X + 1,
                    approvalPosition.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilAsync(
                _ => session.CommitAfterWarningsCallCount == 1,
                TimeSpan.FromSeconds(3),
                "Pointer approval dispatches only the confirmed detached-HEAD transaction");
            Assert.AreEqual(0, session.CommitCallCount);
            Assert.AreEqual("Confirmed commit completed", session.Activity);
            Assert.IsNull(session.LastConfirmedPublishedAmendWarning);
            Assert.AreSame(detachedWarning, session.LastConfirmedDetachedHeadWarning);
            Assert.AreNotSame(session.DetachedHeadWarning, session.LastConfirmedDetachedHeadWarning);
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies merge abort is cancel-first and submits only the exact merge state shown before pointer approval.
    /// </summary>
    [TestMethod]
    public async Task MergeAbort_WithKeyboardAndMouse_ConfirmsExactDisplayedState()
    {
        Assert.IsTrue(ObjectId.TryParseHex(
            "1111111111111111111111111111111111111111"u8,
            out var headObjectId));
        Assert.IsTrue(ObjectId.TryParseHex(
            "2222222222222222222222222222222222222222"u8,
            out var mergeHeadObjectId));
        Assert.IsTrue(ObjectId.TryParseHex(
            "3333333333333333333333333333333333333333"u8,
            out var refreshedMergeHeadObjectId));
        Assert.IsTrue(ObjectId.TryParseHex(
            "4444444444444444444444444444444444444444"u8,
            out var mergeAutostashObjectId));
        var precondition = new RepositoryPrecondition(
            headObjectId,
            RefName.FromBytes("refs/heads/main"u8),
            Enumerable.Repeat((byte)0x11, 32).ToArray());
        var workTreeFingerprint = Enumerable.Repeat((byte)0x22, 32).ToArray();
        var displayedWarning = new MergeAbortWarning(
            precondition,
            [mergeHeadObjectId!],
            workTreeFingerprint,
            mergeAutostashObjectId);
        var refreshedWarning = new MergeAbortWarning(
            precondition,
            [refreshedMergeHeadObjectId!],
            workTreeFingerprint,
            mergeAutostashObjectId);
        var session = new FakeRepositoryWorkspaceSession(
            FakeRepositoryWorkspaceSession.CreateStagedEntry("staged.txt"))
        {
            MergeAbortWarning = displayedWarning,
        };
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
            await automator.WaitUntilTextAsync("Abort merge...", TimeSpan.FromSeconds(3));
            using (var workspace = automator.CreateSnapshot())
            {
                var abortPosition = FindText(workspace, "Abort merge...");
                await automator.ClickAtAsync(
                    abortPosition.X + 1,
                    abortPosition.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilTextAsync("Abort merge?", TimeSpan.FromSeconds(3));
            using (var confirmation = automator.CreateSnapshot())
            {
                Assert.IsTrue(confirmation.ContainsText("1111111111111111111111111111111111111111"));
                Assert.IsTrue(confirmation.ContainsText("2222222222222222222222222222222222222222"));
                Assert.IsTrue(confirmation.ContainsText("MERGE_AUTOSTASH object"));
                Assert.IsTrue(confirmation.ContainsText("4444444444444444444444444444444444444444"));
                Assert.IsTrue(confirmation.ContainsText("merge --abort"));
            }

            await automator.KeyAsync(Hex1bKey.Enter, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Abort merge?"),
                TimeSpan.FromSeconds(3),
                "The first focused action cancels merge-abort confirmation");
            Assert.AreEqual(0, session.AbortMergeCallCount);

            using (var workspace = automator.CreateSnapshot())
            {
                var abortPosition = FindText(workspace, "Abort merge...");
                await automator.ClickAtAsync(
                    abortPosition.X + 1,
                    abortPosition.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilTextAsync("Abort merge?", TimeSpan.FromSeconds(3));
            session.MergeAbortWarning = refreshedWarning;
            using (var confirmation = automator.CreateSnapshot())
            {
                var approvalPosition = FindTextOnLineWith(confirmation, "Abort merge", "Cancel");
                await automator.ClickAtAsync(
                    approvalPosition.X + 1,
                    approvalPosition.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilAsync(
                _ => session.AbortMergeCallCount == 1,
                TimeSpan.FromSeconds(3),
                "Pointer approval dispatches the exact merge state displayed by the dialog");
            Assert.AreSame(displayedWarning, session.LastConfirmedMergeAbortWarning);
            Assert.AreNotSame(refreshedWarning, session.LastConfirmedMergeAbortWarning);
            Assert.AreEqual("Merge aborted", session.Activity);
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies selected-line stage and unstage actions are available through mouse and keyboard input.
    /// </summary>
    [TestMethod]
    public async Task SelectedLines_WithMouseAndKeyboardInput_DispatchesTargetSpecificActions()
    {
        var session = new FakeRepositoryWorkspaceSession(
            FakeRepositoryWorkspaceSession.CreateUnstagedEntry("worktree.txt"),
            FakeRepositoryWorkspaceSession.CreateStagedEntry("index.txt"))
        {
            HasSelectedLines = true,
        };
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
            await automator.WaitUntilTextAsync("Stage lines", TimeSpan.FromSeconds(3));
            using (var worktree = automator.CreateSnapshot())
            {
                var stageLinesPosition = FindText(worktree, "Stage lines");
                await automator.ClickAtAsync(
                    stageLinesPosition.X + 1,
                    stageLinesPosition.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilAsync(
                _ => session.StageSelectedLinesCallCount == 1,
                TimeSpan.FromSeconds(3),
                "The selected-line stage action is pointer-activatable");
            await session.FocusStagedAsync(0, timeout.Token);
            await automator.WaitUntilTextAsync("Unstage lines", TimeSpan.FromSeconds(3));
            await automator.ClickAtAsync(55, 6, MouseButton.Left, timeout.Token);
            await automator.KeyAsync(Hex1bKey.L, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.UnstageSelectedLinesCallCount == 1,
                TimeSpan.FromSeconds(3),
                "L dispatches selected-line unstaging from the index diff");
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies revert defaults to cancellation and every exact scope plus undo is pointer-activatable.
    /// </summary>
    [TestMethod]
    public async Task Revert_WithMouseInput_ConfirmsScopesAndExposesOneLevelUndo()
    {
        var session = new FakeRepositoryWorkspaceSession(
            FakeRepositoryWorkspaceSession.CreateUnstagedEntry("worktree.txt"))
        {
            HasSelectedLines = true,
        };
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
            await automator.WaitUntilTextAsync("Revert...", TimeSpan.FromSeconds(3));
            await OpenRevertConfirmationAsync(automator, timeout.Token);
            await automator.WaitUntilTextAsync("Revert worktree changes?", TimeSpan.FromSeconds(3));
            await automator.KeyAsync(Hex1bKey.Enter, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Revert worktree changes?"),
                TimeSpan.FromSeconds(3),
                "The first focused revert confirmation action closes the modal");
            Assert.AreEqual(0, session.RevertSelectedLinesCallCount);
            Assert.AreEqual(0, session.RevertFocusedHunkCallCount);
            Assert.AreEqual(0, session.RevertFocusedFileCallCount);

            await ConfirmRevertScopeAsync(automator, "Revert lines", timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.RevertSelectedLinesCallCount == 1 && session.CanUndoRevert,
                TimeSpan.FromSeconds(3),
                "Selected-line revert is pointer-activatable after explicit confirmation");
            await ClickUndoRevertAsync(automator, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.UndoRevertCallCount == 1 && !session.CanUndoRevert,
                TimeSpan.FromSeconds(3),
                "The one-level undo action consumes its retained revert");

            await ConfirmRevertScopeAsync(automator, "Revert hunk", timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.RevertFocusedHunkCallCount == 1,
                TimeSpan.FromSeconds(3),
                "Focused-hunk revert is pointer-activatable after explicit confirmation");
            await ClickUndoRevertAsync(automator, timeout.Token);

            await ConfirmRevertScopeAsync(automator, "Revert file", timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.RevertFocusedFileCallCount == 1,
                TimeSpan.FromSeconds(3),
                "Complete-file revert is pointer-activatable after explicit confirmation");
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies untracked hunk preparation is explicit, pointer-activatable, and keyboard reachable.
    /// </summary>
    [TestMethod]
    public async Task UntrackedPatch_WithMouseAndKeyboardInput_DispatchesIntentToAddPreparation()
    {
        var session = new FakeRepositoryWorkspaceSession(
            FakeRepositoryWorkspaceSession.CreateUntrackedEntry("untracked.txt"));
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
            await automator.WaitUntilTextAsync("Prepare hunks", TimeSpan.FromSeconds(3));
            using (var snapshot = automator.CreateSnapshot())
            {
                var preparePosition = FindText(snapshot, "Prepare hunks");
                await automator.ClickAtAsync(
                    preparePosition.X + 1,
                    preparePosition.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilAsync(
                _ => session.PrepareUntrackedPatchCallCount == 1,
                TimeSpan.FromSeconds(3),
                "Untracked hunk preparation is pointer-activatable");
            await automator.ClickAtAsync(55, 6, MouseButton.Left, timeout.Token);
            await automator.KeyAsync(Hex1bKey.P, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.PrepareUntrackedPatchCallCount == 2,
                TimeSpan.FromSeconds(3),
                "P dispatches untracked intent-to-add preparation from the diff");
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies searchable branch selection, tracking choice, and create checkout are keyboard and mouse reachable.
    /// </summary>
    [TestMethod]
    public async Task BranchWindow_WithKeyboardAndMouseInput_FiltersAndCreatesTrackedBranch()
    {
        var remoteBranch = CreateBranch(
            "refs/remotes/origin/team/topic",
            BranchKind.RemoteTracking,
            isCurrent: false);
        var session = new FakeRepositoryWorkspaceSession();
        session.ConfigureBranches(
            CreateBranch("refs/heads/main", BranchKind.Local, isCurrent: true),
            CreateBranch("refs/heads/feature", BranchKind.Local, isCurrent: false),
            remoteBranch);
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
            await automator.WaitUntilTextAsync("F8 Branches", TimeSpan.FromSeconds(3));
            using (var workspace = automator.CreateSnapshot())
            {
                var branches = FindTextOnLineWith(workspace, "Branches", "Git 2.50.0");
                await automator.ClickAtAsync(branches.X + 1, branches.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Branches and linked worktrees", TimeSpan.FromSeconds(3));
            Assert.AreEqual(1, session.LoadBranchesCallCount);
            using (var branchWindow = automator.CreateSnapshot())
            {
                var filter = FindText(branchWindow, "Filter:");
                await automator.ClickAtAsync(filter.X + 9, filter.Y, MouseButton.Left, timeout.Token);
            }

            await automator.TypeAsync("origin/team", timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => session.Branches.VisibleItems.Length == 1 &&
                    ReferenceEquals(session.Branches.FocusedItem?.Branch, remoteBranch) &&
                    snapshot.ContainsText("Merge..."),
                TimeSpan.FromSeconds(3),
                "Filtering publishes the exact remote branch and its settled action hit targets");
            using (var filtered = automator.CreateSnapshot())
            {
                var remote = FindText(filtered, "origin/team/topic");
                await automator.ClickAtAsync(remote.X + 2, remote.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Target:", TimeSpan.FromSeconds(3));
            using (var selected = automator.CreateSnapshot())
            {
                var create = FindText(selected, "New...");
                await automator.ClickAtAsync(create.X + 1, create.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Create local branch", TimeSpan.FromSeconds(3));
            await automator.WaitUntilTextAsync("team/topic", TimeSpan.FromSeconds(3));
            using (var createDialog = automator.CreateSnapshot())
            {
                var name = FindText(createDialog, "Local name:");
                await automator.ClickAtAsync(name.X + 13, name.Y, MouseButton.Left, timeout.Token);
            }

            await new Hex1bTerminalInputSequenceBuilder()
                .Ctrl()
                .Key(Hex1bKey.A)
                .Build()
                .ApplyAsync(terminal, timeout.Token);
            await automator.TypeAsync("team/topic-local", timeout.Token);
            using (var trackingDialog = automator.CreateSnapshot())
            {
                var tracking = FindText(trackingDialog, "Tracking [x] direct");
                await automator.ClickAtAsync(tracking.X + 1, tracking.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Tracking [ ] none", TimeSpan.FromSeconds(3));
            using (var untrackedDialog = automator.CreateSnapshot())
            {
                var tracking = FindText(untrackedDialog, "Tracking [ ] none");
                await automator.ClickAtAsync(tracking.X + 1, tracking.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Tracking [x] direct", TimeSpan.FromSeconds(3));
            using (var ready = automator.CreateSnapshot())
            {
                var create = FindText(ready, "Create and switch");
                await automator.ClickAtAsync(create.X + 1, create.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilAsync(
                _ => session.CreateBranchCallCount == 1,
                TimeSpan.FromSeconds(3),
                "The branch create transaction is mouse-activatable");
            Assert.AreEqual("team/topic-local", session.LastBranchName);
            Assert.AreEqual("origin/team/topic", session.LastBranch?.ShortName.DisplayText);
            Assert.AreEqual("Created tracked branch", session.Activity);
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies exact merge planning, cancel-first behavior, and every typed option are mouse reachable at 80 by 24.
    /// </summary>
    [TestMethod]
    public async Task MergeDialog_AtEightyByTwentyFour_SubmitsExactPlanAndTypedOptions()
    {
        var main = CreateBranch("refs/heads/main", BranchKind.Local, isCurrent: true);
        var feature = CreateBranch("refs/heads/feature", BranchKind.Local, isCurrent: false);
        var session = new FakeRepositoryWorkspaceSession();
        session.ConfigureBranches(main, feature);
        var view = new RepositoryWorkspaceView(
            new GitSailShellOptions(ApplicationMode.Gui, WorkingDirectory: null),
            session,
            CancellationToken.None);
        Hex1bApp? application = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(80, 24)
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
            await automator.KeyAsync(Hex1bKey.F8, timeout.Token);
            await automator.WaitUntilTextAsync("Branches and linked worktrees", TimeSpan.FromSeconds(3));
            using (var branches = automator.CreateSnapshot())
            {
                var filter = FindText(branches, "Filter:");
                await automator.ClickAtAsync(filter.X + 10, filter.Y, MouseButton.Left, timeout.Token);
            }

            await automator.TypeAsync("feature", timeout.Token);
            await automator.WaitUntilTextAsync("feature", TimeSpan.FromSeconds(3));
            await automator.WaitUntilTextAsync("Merge...", TimeSpan.FromSeconds(3));
            using (var branches = automator.CreateSnapshot())
            {
                var merge = FindText(branches, "Merge...");
                await automator.ClickAtAsync(merge.X + 1, merge.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Merge exact selected branch?", TimeSpan.FromSeconds(3));
            Assert.AreEqual(1, session.PrepareMergeCallCount);
            await automator.KeyAsync(Hex1bKey.Enter, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Merge exact selected branch?"),
                TimeSpan.FromSeconds(3),
                "The first focused merge-dialog action cancels without mutation");
            Assert.AreEqual(0, session.MergeCallCount);

            using (var branches = automator.CreateSnapshot())
            {
                var merge = FindText(branches, "Merge...");
                await automator.ClickAtAsync(merge.X + 1, merge.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Merge exact selected branch?", TimeSpan.FromSeconds(3));
            using (var dialog = automator.CreateSnapshot())
            {
                Assert.IsTrue(dialog.ContainsText(feature.TargetObjectId.ToString()));
                Assert.IsTrue(dialog.ContainsText("2 current-only, 3 incoming-only"));
                Assert.IsTrue(dialog.ContainsText("Merge exact object"));
                var fastForward = FindText(dialog, "Fast-forward: Git config");
                await automator.ClickAtAsync(fastForward.X + 1, fastForward.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Fast-forward: only", TimeSpan.FromSeconds(3));
            using (var dialog = automator.CreateSnapshot())
            {
                var fastForward = FindText(dialog, "Fast-forward: only");
                await automator.ClickAtAsync(fastForward.X + 1, fastForward.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Fast-forward: create merge commit", TimeSpan.FromSeconds(3));
            using (var dialog = automator.CreateSnapshot())
            {
                var strategy = FindText(dialog, "Strategy: Git default");
                await automator.ClickAtAsync(strategy.X + 1, strategy.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Strategy: ort", TimeSpan.FromSeconds(3));
            for (var index = 0; index < 2; index++)
            {
                using var dialog = automator.CreateSnapshot();
                var preference = FindText(
                    dialog,
                    index == 0 ? "Conflicts: normal" : "Conflicts: favor ours");
                await automator.ClickAtAsync(preference.X + 1, preference.Y, MouseButton.Left, timeout.Token);
                await automator.WaitUntilTextAsync(
                    index == 0 ? "Conflicts: favor ours" : "Conflicts: favor theirs",
                    TimeSpan.FromSeconds(3));
            }

            using (var dialog = automator.CreateSnapshot())
            {
                var squash = FindText(dialog, "Squash [ ]");
                await automator.ClickAtAsync(squash.X + 1, squash.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Squash [x]", TimeSpan.FromSeconds(3));
            using (var dialog = automator.CreateSnapshot())
            {
                var stop = FindText(dialog, "Stop before commit [ ]");
                await automator.ClickAtAsync(stop.X + 1, stop.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Stop before commit [x]", TimeSpan.FromSeconds(3));
            using (var dialog = automator.CreateSnapshot())
            {
                Assert.IsTrue(dialog.ContainsText("Squash [ ]"));
                var autoStash = FindText(dialog, "Autostash: Git config");
                await automator.ClickAtAsync(autoStash.X + 1, autoStash.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Autostash: on", TimeSpan.FromSeconds(3));
            for (var index = 0; index < 2; index++)
            {
                using var dialog = automator.CreateSnapshot();
                var rerere = FindText(
                    dialog,
                    index == 0 ? "Rerere update: Git config" : "Rerere update: on");
                await automator.ClickAtAsync(rerere.X + 1, rerere.Y, MouseButton.Left, timeout.Token);
                await automator.WaitUntilTextAsync(
                    index == 0 ? "Rerere update: on" : "Rerere update: off",
                    TimeSpan.FromSeconds(3));
            }

            using (var dialog = automator.CreateSnapshot())
            {
                var verify = FindText(dialog, "Verify signatures: Git config");
                await automator.ClickAtAsync(verify.X + 1, verify.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Verify signatures: on", TimeSpan.FromSeconds(3));
            using (var dialog = automator.CreateSnapshot())
            {
                var merge = FindTextOnLineWith(dialog, "Merge exact object", "Cancel");
                await automator.ClickAtAsync(merge.X + 1, merge.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilAsync(
                _ => session.MergeCallCount == 1,
                TimeSpan.FromSeconds(3),
                "The exact merge confirmation is pointer activatable");
            Assert.AreSame(feature, session.LastMergePlan?.Source);
            Assert.AreEqual(MergeFastForwardMode.NoFastForward, session.LastMergeOptions?.FastForwardMode);
            Assert.AreEqual(MergeStrategy.Ort, session.LastMergeOptions?.Strategy);
            Assert.AreEqual(MergeConflictPreference.Theirs, session.LastMergeOptions?.ConflictPreference);
            Assert.IsFalse(session.LastMergeOptions?.Squash);
            Assert.IsTrue(session.LastMergeOptions?.StopBeforeCommit);
            Assert.AreEqual(GitOptionOverride.Enabled, session.LastMergeOptions?.AutoStash);
            Assert.AreEqual(GitOptionOverride.Disabled, session.LastMergeOptions?.RerereAutoUpdate);
            Assert.AreEqual(GitOptionOverride.Enabled, session.LastMergeOptions?.VerifySignatures);
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies searchable remotes, redacted destinations, typed fetch options, and output tabs at 80 by 24.
    /// </summary>
    [TestMethod]
    public async Task RemoteWorkspace_AtEightyByTwentyFour_FiltersAndFetchesWithTypedOptions()
    {
        var origin = CreateRemote("origin", "ssh://developer@example.invalid/team/origin.git");
        var upstream = CreateRemote(
            "upstream",
            "https://person:password@example.invalid/team/upstream.git?token=secret");
        var session = new FakeRepositoryWorkspaceSession();
        session.ConfigureRemotes(origin, upstream);
        var view = new RepositoryWorkspaceView(
            new GitSailShellOptions(ApplicationMode.Gui, WorkingDirectory: null),
            session,
            CancellationToken.None);
        Hex1bApp? application = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(80, 24)
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
            await automator.KeyAsync(Hex1bKey.F2, timeout.Token);
            await automator.WaitUntilTextAsync("Command palette", TimeSpan.FromSeconds(3));
            using (var palette = automator.CreateSnapshot())
            {
                var filter = FindText(palette, "Find action:");
                await automator.ClickAtAsync(filter.X + 14, filter.Y, MouseButton.Left, timeout.Token);
            }

            await automator.TypeAsync("remotes", timeout.Token);
            await automator.WaitUntilTextAsync("Remote: Remotes and transport", TimeSpan.FromSeconds(3));
            await automator.KeyAsync(Hex1bKey.Enter, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.LoadRemotesCallCount == 1,
                TimeSpan.FromSeconds(3),
                "Submitting the palette opens and loads the complete remote workspace");
            await automator.WaitUntilTextAsync("Remotes and transport", TimeSpan.FromSeconds(3));
            Assert.AreEqual(1, session.LoadRemotesCallCount);
            using (var remotes = automator.CreateSnapshot())
            {
                Assert.IsTrue(remotes.ContainsText("origin"));
                Assert.IsTrue(remotes.ContainsText("upstream"));
                Assert.IsTrue(remotes.ContainsText("stdout"));
                Assert.IsTrue(remotes.ContainsText("stderr / progress"));
                var filter = FindText(remotes, "Filter:");
                await automator.ClickAtAsync(filter.X + 9, filter.Y, MouseButton.Left, timeout.Token);
            }

            await automator.TypeAsync("upstream", timeout.Token);
            await automator.WaitUntilTextAsync("https://example.invalid/team/upstream.git?<redacted>", TimeSpan.FromSeconds(3));
            using (var filtered = automator.CreateSnapshot())
            {
                Assert.IsFalse(filtered.ContainsText("person"));
                Assert.IsFalse(filtered.ContainsText("password"));
                Assert.IsFalse(filtered.ContainsText("token=secret"));
                var fetch = FindText(filtered, "Fetch...");
                await automator.ClickAtAsync(fetch.X + 1, fetch.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Fetch upstream?", TimeSpan.FromSeconds(3));
            await automator.KeyAsync(Hex1bKey.Enter, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Fetch upstream?"),
                TimeSpan.FromSeconds(3),
                "The first focused fetch action cancels without transport");
            Assert.AreEqual(0, session.FetchRemoteCallCount);
            using (var remotes = automator.CreateSnapshot())
            {
                var fetch = FindText(remotes, "Fetch...");
                await automator.ClickAtAsync(fetch.X + 1, fetch.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Fetch upstream?", TimeSpan.FromSeconds(3));
            for (var index = 0; index < 2; index++)
            {
                using var dialog = automator.CreateSnapshot();
                var prune = FindText(dialog, index == 0 ? "Prune: Git config" : "Prune: on");
                await automator.ClickAtAsync(prune.X + 1, prune.Y, MouseButton.Left, timeout.Token);
                await automator.WaitUntilTextAsync(
                    index == 0 ? "Prune: on" : "Prune: off",
                    TimeSpan.FromSeconds(3));
            }

            for (var index = 0; index < 2; index++)
            {
                using var dialog = automator.CreateSnapshot();
                var tags = FindText(dialog, index == 0 ? "Tags: Git config" : "Tags: all");
                await automator.ClickAtAsync(tags.X + 1, tags.Y, MouseButton.Left, timeout.Token);
                await automator.WaitUntilTextAsync(
                    index == 0 ? "Tags: all" : "Tags: none",
                    TimeSpan.FromSeconds(3));
            }

            using (var dialog = automator.CreateSnapshot())
            {
                var fetch = FindTextOnLineWith(dialog, "Fetch exact remote", "Cancel");
                await automator.ClickAtAsync(fetch.X + 1, fetch.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilAsync(
                _ => session.FetchRemoteCallCount == 1,
                TimeSpan.FromSeconds(3),
                "The exact typed fetch transaction is pointer activatable");
            Assert.AreSame(upstream, session.LastRemote);
            Assert.AreEqual(GitOptionOverride.Disabled, session.LastFetchOptions?.Prune);
            Assert.AreEqual(FetchTagMode.None, session.LastFetchOptions?.Tags);

            await automator.KeyAsync(Hex1bKey.F2, timeout.Token);
            await automator.WaitUntilTextAsync("Command palette", TimeSpan.FromSeconds(3));
            using (var palette = automator.CreateSnapshot())
            {
                var filter = FindText(palette, "Find action:");
                await automator.ClickAtAsync(filter.X + 14, filter.Y, MouseButton.Left, timeout.Token);
            }

            await automator.TypeAsync("remotes", timeout.Token);
            await automator.WaitUntilTextAsync("Remote: Remotes and transport", TimeSpan.FromSeconds(3));
            await automator.KeyAsync(Hex1bKey.Enter, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.LoadRemotesCallCount == 2,
                TimeSpan.FromSeconds(3),
                "The completed output remains available when the remote workspace reopens");
            await automator.WaitUntilTextAsync("fake stdout", TimeSpan.FromSeconds(3));
            using (var remotes = automator.CreateSnapshot())
            {
                var standardError = FindText(remotes, "stderr / progress");
                await automator.ClickAtAsync(
                    standardError.X + 1,
                    standardError.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilTextAsync("fake stderr", TimeSpan.FromSeconds(3));
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies every remaining visible remote mutation is mouse reachable and destructive choices cancel first.
    /// </summary>
    [TestMethod]
    public async Task RemoteWorkspace_WithMouseInput_AddsFetchesAllPrunesAndRemovesExactTargets()
    {
        var origin = CreateRemote("origin", "ssh://developer@example.invalid/team/origin.git");
        var session = new FakeRepositoryWorkspaceSession();
        session.ConfigureRemotes(origin);
        var view = new RepositoryWorkspaceView(
            new GitSailShellOptions(ApplicationMode.Gui, WorkingDirectory: null),
            session,
            CancellationToken.None);
        Hex1bApp? application = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
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
            await OpenRemoteWorkspaceWithMouseAsync(automator, session, 1, timeout.Token);
            using (var remotes = automator.CreateSnapshot())
            {
                var add = FindText(remotes, "Add...");
                await automator.ClickAtAsync(add.X + 1, add.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Add remote", TimeSpan.FromSeconds(3));
            using (var dialog = automator.CreateSnapshot())
            {
                var name = FindText(dialog, "Name:");
                await automator.ClickAtAsync(name.X + 7, name.Y, MouseButton.Left, timeout.Token);
            }

            await new Hex1bTerminalInputSequenceBuilder()
                .Ctrl()
                .Key(Hex1bKey.A)
                .Build()
                .ApplyAsync(terminal, timeout.Token);
            await automator.TypeAsync("backup", timeout.Token);
            using (var dialog = automator.CreateSnapshot())
            {
                var url = FindText(dialog, "URL:");
                await automator.ClickAtAsync(url.X + 7, url.Y, MouseButton.Left, timeout.Token);
            }

            const string enteredUrl = "https://person:password@example.invalid/team/backup.git?token=secret";
            await automator.TypeAsync(enteredUrl, timeout.Token);
            using (var dialog = automator.CreateSnapshot())
            {
                var add = FindTextOnLineWith(dialog, "Add exact remote", "Cancel");
                await automator.ClickAtAsync(add.X + 1, add.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilAsync(
                _ => session.AddRemoteCallCount == 1,
                TimeSpan.FromSeconds(3),
                "The typed remote-add transaction is pointer activatable");
            Assert.AreEqual("backup", session.LastRemoteName);
            Assert.AreEqual(enteredUrl, session.LastRemoteUrl);

            await OpenRemoteWorkspaceWithMouseAsync(automator, session, 2, timeout.Token);
            using (var remotes = automator.CreateSnapshot())
            {
                var fetchAll = FindText(remotes, "Fetch all...");
                await automator.ClickAtAsync(fetchAll.X + 1, fetchAll.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Fetch every configured remote?", TimeSpan.FromSeconds(3));
            await automator.KeyAsync(Hex1bKey.Enter, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Fetch every configured remote?"),
                TimeSpan.FromSeconds(3),
                "Fetch-all defaults to cancel without transport");
            Assert.AreEqual(0, session.FetchAllRemotesCallCount);
            using (var remotes = automator.CreateSnapshot())
            {
                var fetchAll = FindText(remotes, "Fetch all...");
                await automator.ClickAtAsync(fetchAll.X + 1, fetchAll.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Fetch every configured remote?", TimeSpan.FromSeconds(3));
            using (var dialog = automator.CreateSnapshot())
            {
                var fetchAll = FindTextOnLineWith(dialog, "Fetch all exact remotes", "Cancel");
                await automator.ClickAtAsync(fetchAll.X + 1, fetchAll.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilAsync(
                _ => session.FetchAllRemotesCallCount == 1,
                TimeSpan.FromSeconds(3),
                "The typed fetch-all transaction is pointer activatable");
            Assert.AreEqual(GitOptionOverride.Configured, session.LastFetchOptions?.Prune);
            Assert.AreEqual(FetchTagMode.Configured, session.LastFetchOptions?.Tags);

            await OpenRemoteWorkspaceWithMouseAsync(automator, session, 3, timeout.Token);
            using (var remotes = automator.CreateSnapshot())
            {
                var prune = FindText(remotes, "Prune...");
                await automator.ClickAtAsync(prune.X + 1, prune.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Prune stale remote refs?", TimeSpan.FromSeconds(3));
            Assert.AreEqual(1, session.PreparePruneRemoteCallCount);
            await automator.KeyAsync(Hex1bKey.Enter, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Prune stale remote refs?"),
                TimeSpan.FromSeconds(3),
                "Prune defaults to cancel without deleting refs");
            Assert.AreEqual(0, session.PruneRemoteCallCount);
            using (var remotes = automator.CreateSnapshot())
            {
                var prune = FindText(remotes, "Prune...");
                await automator.ClickAtAsync(prune.X + 1, prune.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Prune stale remote refs?", TimeSpan.FromSeconds(3));
            using (var dialog = automator.CreateSnapshot())
            {
                Assert.IsTrue(dialog.ContainsText("[would prune] origin/stale"));
                var prune = FindTextOnLineWith(dialog, "Prune exact remote", "Cancel");
                await automator.ClickAtAsync(prune.X + 1, prune.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilAsync(
                _ => session.PruneRemoteCallCount == 1,
                TimeSpan.FromSeconds(3),
                "The reviewed exact prune plan is pointer activatable");
            Assert.AreSame(origin, session.LastRemote);

            await OpenRemoteWorkspaceWithMouseAsync(automator, session, 4, timeout.Token);
            using (var remotes = automator.CreateSnapshot())
            {
                var remove = FindText(remotes, "Remove...");
                await automator.ClickAtAsync(remove.X + 1, remove.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Remove configured remote?", TimeSpan.FromSeconds(3));
            await automator.KeyAsync(Hex1bKey.Enter, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Remove configured remote?"),
                TimeSpan.FromSeconds(3),
                "Remove defaults to cancel without configuration mutation");
            Assert.AreEqual(0, session.RemoveRemoteCallCount);
            using (var remotes = automator.CreateSnapshot())
            {
                var remove = FindText(remotes, "Remove...");
                await automator.ClickAtAsync(remove.X + 1, remove.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Remove configured remote?", TimeSpan.FromSeconds(3));
            using (var dialog = automator.CreateSnapshot())
            {
                var remove = FindTextOnLineWith(dialog, "Remove exact remote", "Cancel");
                await automator.ClickAtAsync(remove.X + 1, remove.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilAsync(
                _ => session.RemoveRemoteCallCount == 1,
                TimeSpan.FromSeconds(3),
                "The exact remote removal is pointer activatable");
            Assert.AreSame(origin, session.LastRemote);
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies the compact push dialog previews every exact identity, redacts URLs, cancels first, and submits typed options.
    /// </summary>
    [TestMethod]
    public async Task PushDialog_AtEightyByTwentyFour_PreviewsExactPlanAndPushesWithLeases()
    {
        var remote = CreateRemote(
            "origin",
            "https://person:password@example.invalid/team/repository.git?token=secret");
        var plan = CreatePushPlan(remote, PushRelationship.FastForward, wouldSetUpstream: false);
        var session = new FakeRepositoryWorkspaceSession();
        session.ConfigureRemotes(remote);
        session.ConfigurePushPlan(plan);
        var view = new RepositoryWorkspaceView(
            new GitSailShellOptions(ApplicationMode.Gui, WorkingDirectory: null),
            session,
            CancellationToken.None);
        Hex1bApp? application = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(80, 24)
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
            await OpenRemoteWorkspaceThroughPaletteAsync(automator, session, 1, timeout.Token);
            using (var remotes = automator.CreateSnapshot())
            {
                var push = FindText(remotes, "Push...");
                await automator.ClickAtAsync(push.X + 1, push.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Push exact Git default plan?", TimeSpan.FromSeconds(3));
            Assert.AreEqual(1, session.PreparePushCallCount);
            using (var dialog = automator.CreateSnapshot())
            {
                Assert.IsTrue(dialog.ContainsText("refs/heads/main"));
                Assert.IsTrue(dialog.ContainsText(plan.Updates[0].SourceObjectId!.ToString()));
                Assert.IsTrue(dialog.ContainsText(
                    plan.Updates[0].Destinations[0].ExpectedObjectId!.ToString()));
                Assert.IsTrue(dialog.ContainsText("https://example.invalid/team/repository.git?<redacted>"));
                Assert.IsTrue(dialog.ContainsText("fast-forward"));
                Assert.IsTrue(dialog.ContainsText("Introduced commits: 3"));
                Assert.IsFalse(dialog.ContainsText("password"));
                Assert.IsFalse(dialog.ContainsText("token=secret"));
            }

            await automator.KeyAsync(Hex1bKey.Enter, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Push exact Git default plan?"),
                TimeSpan.FromSeconds(3),
                "The first focused push action cancels without transport");
            Assert.AreEqual(0, session.PushCallCount);
            using (var remotes = automator.CreateSnapshot())
            {
                var push = FindText(remotes, "Push...");
                await automator.ClickAtAsync(push.X + 1, push.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Push exact Git default plan?", TimeSpan.FromSeconds(3));
            using (var dialog = automator.CreateSnapshot())
            {
                var upstream = FindText(dialog, "Set upstream [ ]");
                await automator.ClickAtAsync(upstream.X + 1, upstream.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Set upstream [x]", TimeSpan.FromSeconds(3));
            using (var dialog = automator.CreateSnapshot())
            {
                var push = FindTextOnLineWith(dialog, "Push exact plan", "Cancel");
                await automator.ClickAtAsync(push.X + 1, push.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilAsync(
                _ => session.PushCallCount == 1,
                TimeSpan.FromSeconds(3),
                "The exact normal push is pointer activatable");
            Assert.AreSame(plan, session.LastPushPlan);
            Assert.AreEqual(PushSafetyMode.Normal, session.LastPushOptions?.SafetyMode);
            Assert.IsTrue(session.LastPushOptions?.SetUpstream);
            Assert.AreEqual(GitOptionOverride.Configured, session.LastPushOptions?.FollowTags);
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies rewrites reject normal mode, accept exact leases, and require a second warning before unleased force.
    /// </summary>
    [TestMethod]
    public async Task PushDialog_WithRewrite_RequiresLeaseOrSecondForceConfirmation()
    {
        var remote = CreateRemote("origin", "ssh://developer@example.invalid/team/repository.git");
        var plan = CreatePushPlan(remote, PushRelationship.NonFastForward, wouldSetUpstream: false);
        var session = new FakeRepositoryWorkspaceSession();
        session.ConfigureRemotes(remote);
        session.ConfigurePushPlan(plan);
        var view = new RepositoryWorkspaceView(
            new GitSailShellOptions(ApplicationMode.Gui, WorkingDirectory: null),
            session,
            CancellationToken.None);
        Hex1bApp? application = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(18));
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
            await OpenRemoteWorkspaceWithMouseAsync(automator, session, 1, timeout.Token);
            await OpenPushDialogWithMouseAsync(automator, timeout.Token);
            using (var dialog = automator.CreateSnapshot())
            {
                var push = FindTextOnLineWith(dialog, "Push exact plan", "Cancel");
                await automator.ClickAtAsync(push.X + 1, push.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync(
                "Select explicit leases for non-fast-forward updates or deletions.",
                TimeSpan.FromSeconds(3));
            Assert.AreEqual(0, session.PushCallCount);
            using (var dialog = automator.CreateSnapshot())
            {
                var safety = FindText(dialog, "Safety: normal with exact leases");
                await automator.ClickAtAsync(safety.X + 1, safety.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync(
                "Safety: allow rewrite with exact leases",
                TimeSpan.FromSeconds(3));
            using (var dialog = automator.CreateSnapshot())
            {
                var push = FindTextOnLineWith(dialog, "Push exact plan", "Cancel");
                await automator.ClickAtAsync(push.X + 1, push.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilAsync(
                _ => session.PushCallCount == 1,
                TimeSpan.FromSeconds(3),
                "A confirmed rewrite uses explicit expected-OID leases");
            Assert.AreEqual(PushSafetyMode.ExplicitLease, session.LastPushOptions?.SafetyMode);

            await OpenRemoteWorkspaceWithMouseAsync(automator, session, 2, timeout.Token);
            await OpenPushDialogWithMouseAsync(automator, timeout.Token);
            for (var index = 0; index < 2; index++)
            {
                using var dialog = automator.CreateSnapshot();
                var safety = FindText(
                    dialog,
                    index == 0
                        ? "Safety: normal with exact leases"
                        : "Safety: allow rewrite with exact leases");
                await automator.ClickAtAsync(safety.X + 1, safety.Y, MouseButton.Left, timeout.Token);
                await automator.WaitUntilTextAsync(
                    index == 0
                        ? "Safety: allow rewrite with exact leases"
                        : "Safety: force without leases",
                    TimeSpan.FromSeconds(3));
            }

            using (var dialog = automator.CreateSnapshot())
            {
                var continueForce = FindTextOnLineWith(dialog, "Continue to force warning", "Cancel");
                await automator.ClickAtAsync(
                    continueForce.X + 1,
                    continueForce.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilTextAsync(
                "Force push without an expected-OID lease?",
                TimeSpan.FromSeconds(3));
            await automator.KeyAsync(Hex1bKey.Enter, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Force push without an expected-OID lease?"),
                TimeSpan.FromSeconds(3),
                "The second destructive warning also defaults to cancel");
            Assert.AreEqual(1, session.PushCallCount);

            await OpenPushDialogWithMouseAsync(automator, timeout.Token);
            for (var index = 0; index < 2; index++)
            {
                using var dialog = automator.CreateSnapshot();
                var safety = FindText(
                    dialog,
                    index == 0
                        ? "Safety: normal with exact leases"
                        : "Safety: allow rewrite with exact leases");
                await automator.ClickAtAsync(safety.X + 1, safety.Y, MouseButton.Left, timeout.Token);
                await automator.WaitUntilTextAsync(
                    index == 0
                        ? "Safety: allow rewrite with exact leases"
                        : "Safety: force without leases",
                    TimeSpan.FromSeconds(3));
            }

            using (var dialog = automator.CreateSnapshot())
            {
                var continueForce = FindTextOnLineWith(dialog, "Continue to force warning", "Cancel");
                await automator.ClickAtAsync(
                    continueForce.X + 1,
                    continueForce.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilTextAsync("Force without lease", TimeSpan.FromSeconds(3));
            using (var warning = automator.CreateSnapshot())
            {
                var force = FindTextOnLineWith(warning, "Force without lease", "Cancel");
                await automator.ClickAtAsync(force.X + 1, force.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilAsync(
                _ => session.PushCallCount == 2,
                TimeSpan.FromSeconds(3),
                "Unleased force requires both pointer confirmations");
            Assert.AreEqual(PushSafetyMode.Force, session.LastPushOptions?.SafetyMode);
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies searchable stash preview, typed create options, and cancel-first pop and drop are mouse reachable.
    /// </summary>
    [TestMethod]
    public async Task StashWindow_WithKeyboardAndMouseInput_UsesExactFocusedEntryAndConfirmations()
    {
        var first = CreateStash(0, '1', "On main: ordinary work");
        var release = CreateStash(1, '2', "On main: release candidate");
        var session = new FakeRepositoryWorkspaceSession();
        session.ConfigureStashes(first, release);
        var view = new RepositoryWorkspaceView(
            new GitSailShellOptions(ApplicationMode.Gui, WorkingDirectory: null),
            session,
            CancellationToken.None);
        Hex1bApp? application = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
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
            await automator.WaitUntilTextAsync("F9 Stashes", TimeSpan.FromSeconds(3));
            using (var workspace = automator.CreateSnapshot())
            {
                var stashes = FindTextOnLineWith(workspace, "Stashes", "Git 2.50.0");
                await automator.ClickAtAsync(stashes.X + 1, stashes.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Stashes and exact patches", TimeSpan.FromSeconds(3));
            Assert.AreEqual(1, session.LoadStashesCallCount);
            using (var stashWindow = automator.CreateSnapshot())
            {
                var filter = FindText(stashWindow, "Filter:");
                await automator.ClickAtAsync(filter.X + 20, filter.Y, MouseButton.Left, timeout.Token);
            }

            await automator.TypeAsync("release", timeout.Token);
            await automator.WaitUntilTextAsync("release candidate", TimeSpan.FromSeconds(3));
            await automator.WaitUntilTextAsync("+On main: release candidate", TimeSpan.FromSeconds(3));
            using (var filtered = automator.CreateSnapshot())
            {
                var row = FindText(filtered, "release candidate");
                await automator.ClickAtAsync(row.X + 2, row.Y, MouseButton.Left, timeout.Token);
                var create = FindText(filtered, "New...");
                await automator.ClickAtAsync(create.X + 1, create.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Save current changes to a stash", TimeSpan.FromSeconds(3));
            using (var createDialog = automator.CreateSnapshot())
            {
                var message = FindText(createDialog, "Message:");
                await automator.ClickAtAsync(message.X + 10, message.Y, MouseButton.Left, timeout.Token);
            }

            await automator.TypeAsync("release snapshot", timeout.Token);
            using (var createDialog = automator.CreateSnapshot())
            {
                var files = FindText(createDialog, "Files: tracked");
                await automator.ClickAtAsync(files.X + 1, files.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Files: +untracked", TimeSpan.FromSeconds(3));
            using (var createDialog = automator.CreateSnapshot())
            {
                var files = FindText(createDialog, "Files: +untracked");
                await automator.ClickAtAsync(files.X + 1, files.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Files: +ignored", TimeSpan.FromSeconds(3));
            using (var createDialog = automator.CreateSnapshot())
            {
                var keepIndex = FindText(createDialog, "Keep index [ ]");
                await automator.ClickAtAsync(keepIndex.X + 1, keepIndex.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Keep index [x]", TimeSpan.FromSeconds(3));
            using (var createDialog = automator.CreateSnapshot())
            {
                var save = FindTextOnLineWith(createDialog, "Save stash", "Cancel");
                await automator.ClickAtAsync(save.X + 1, save.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilAsync(
                _ => session.CreateStashCallCount == 1,
                TimeSpan.FromSeconds(3),
                "The typed stash-create transaction is pointer-activatable");
            Assert.AreEqual("release snapshot", session.LastStashCreateOptions?.Message);
            Assert.AreEqual(StashFileScope.IncludeIgnored, session.LastStashCreateOptions?.FileScope);
            Assert.IsTrue(session.LastStashCreateOptions?.KeepIndex);
            Assert.IsFalse(session.LastStashCreateOptions?.StagedOnly);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Stashes and exact patches"),
                TimeSpan.FromSeconds(3),
                "The completed create action closes its parent stash window");

            using (var workspace = automator.CreateSnapshot())
            {
                var stashes = FindTextOnLineWith(workspace, "Stashes", "Git 2.50.0");
                await automator.ClickAtAsync(stashes.X + 1, stashes.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Stashes and exact patches", TimeSpan.FromSeconds(3));
            using (var stashWindow = automator.CreateSnapshot())
            {
                var pop = FindText(stashWindow, "Pop...");
                await automator.ClickAtAsync(pop.X + 1, pop.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Pop stash?", TimeSpan.FromSeconds(3));
            await automator.KeyAsync(Hex1bKey.Enter, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Pop stash?"),
                TimeSpan.FromSeconds(3),
                "The first focused pop action cancels without mutation");
            Assert.AreEqual(0, session.PopStashCallCount);
            using (var stashWindow = automator.CreateSnapshot())
            {
                var pop = FindText(stashWindow, "Pop...");
                await automator.ClickAtAsync(pop.X + 1, pop.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Pop stash?", TimeSpan.FromSeconds(3));
            using (var popDialog = automator.CreateSnapshot())
            {
                Assert.IsTrue(popDialog.ContainsText(release.ObjectId.ToString()));
                var restore = FindText(popDialog, "Restore index [ ]");
                await automator.ClickAtAsync(restore.X + 1, restore.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Restore index [x]", TimeSpan.FromSeconds(3));
            using (var popDialog = automator.CreateSnapshot())
            {
                var pop = FindTextOnLineWith(popDialog, "Pop stash", "Cancel");
                await automator.ClickAtAsync(pop.X + 1, pop.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilAsync(
                _ => session.PopStashCallCount == 1,
                TimeSpan.FromSeconds(3),
                "The confirmed pop dispatches the exact focused stash");
            Assert.AreSame(release, session.LastStash);
            Assert.IsTrue(session.LastStashRestoreIndex);

            using (var workspace = automator.CreateSnapshot())
            {
                var stashes = FindTextOnLineWith(workspace, "Stashes", "Git 2.50.0");
                await automator.ClickAtAsync(stashes.X + 1, stashes.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Stashes and exact patches", TimeSpan.FromSeconds(3));
            using (var stashWindow = automator.CreateSnapshot())
            {
                var drop = FindText(stashWindow, "Drop...");
                await automator.ClickAtAsync(drop.X + 1, drop.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Drop stash?", TimeSpan.FromSeconds(3));
            await automator.KeyAsync(Hex1bKey.Enter, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Drop stash?"),
                TimeSpan.FromSeconds(3),
                "The first focused drop action cancels without deletion");
            Assert.AreEqual(0, session.DropStashCallCount);
            using (var stashWindow = automator.CreateSnapshot())
            {
                var drop = FindText(stashWindow, "Drop...");
                await automator.ClickAtAsync(drop.X + 1, drop.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Drop stash?", TimeSpan.FromSeconds(3));
            using (var dropDialog = automator.CreateSnapshot())
            {
                var drop = FindTextOnLineWith(dropDialog, "Drop stash", "Cancel");
                await automator.ClickAtAsync(drop.X + 1, drop.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilAsync(
                _ => session.DropStashCallCount == 1,
                TimeSpan.FromSeconds(3),
                "The confirmed drop dispatches the exact focused stash");
            Assert.AreSame(release, session.LastStash);
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies the stash list, patch, and every primary action remain reachable at 80 by 24 cells.
    /// </summary>
    [TestMethod]
    public async Task StashWindow_AtEightyByTwentyFour_RemainsCompleteAndMouseReachable()
    {
        var session = new FakeRepositoryWorkspaceSession();
        session.ConfigureStashes(CreateStash(0, '3', "On main: compact terminal"));
        var view = new RepositoryWorkspaceView(
            new GitSailShellOptions(ApplicationMode.Gui, WorkingDirectory: null),
            session,
            CancellationToken.None);
        Hex1bApp? application = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(80, 24)
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
            await automator.KeyAsync(Hex1bKey.F9, timeout.Token);
            await automator.WaitUntilTextAsync("Stashes and exact patches", TimeSpan.FromSeconds(3));
            using (var compact = automator.CreateSnapshot())
            {
                Assert.IsTrue(compact.ContainsText("Filter:"));
                Assert.IsTrue(compact.ContainsText("compact terminal"));
                Assert.IsTrue(compact.ContainsText("Cancel"));
                Assert.IsTrue(compact.ContainsText("Refresh"));
                Assert.IsTrue(compact.ContainsText("New..."));
                Assert.IsTrue(compact.ContainsText("Apply..."));
                Assert.IsTrue(compact.ContainsText("Pop..."));
                Assert.IsTrue(compact.ContainsText("Drop..."));
                var create = FindText(compact, "New...");
                await automator.ClickAtAsync(create.X + 1, create.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Save current changes to a stash", TimeSpan.FromSeconds(3));
            using (var createDialog = automator.CreateSnapshot())
            {
                Assert.IsTrue(createDialog.ContainsText("Message:"));
                Assert.IsTrue(createDialog.ContainsText("Files: tracked"));
                Assert.IsTrue(createDialog.ContainsText("Keep index [ ]"));
                Assert.IsTrue(createDialog.ContainsText("Staged only [ ]"));
                Assert.IsTrue(createDialog.ContainsText("Save stash"));
                var cancel = FindTextOnLineWith(createDialog, "Cancel", "Save stash");
                await automator.ClickAtAsync(cancel.X + 1, cancel.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilAsync(
                snapshot => snapshot.ContainsText("Stashes and exact patches") &&
                    !snapshot.ContainsText("Save current changes to a stash"),
                TimeSpan.FromSeconds(3),
                "The compact create dialog cancels back to the complete stash workspace");
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies F1 help and the F2 searchable command palette remain complete and mouse reachable at 80 by 24.
    /// </summary>
    [TestMethod]
    public async Task HelpAndCommandPalette_AtEightyByTwentyFour_SearchAndRunLiveActions()
    {
        var session = new FakeRepositoryWorkspaceSession();
        session.ConfigureStashes(CreateStash(0, '4', "On main: palette target"));
        var view = new RepositoryWorkspaceView(
            new GitSailShellOptions(ApplicationMode.Gui, WorkingDirectory: null),
            session,
            CancellationToken.None);
        Hex1bApp? application = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(80, 24)
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
            await automator.KeyAsync(Hex1bKey.F1, timeout.Token);
            await automator.WaitUntilTextAsync("Help and keyboard reference", TimeSpan.FromSeconds(3));
            using (var help = automator.CreateSnapshot())
            {
                Assert.IsTrue(help.ContainsText("F2 searchable commands"));
                Assert.IsTrue(help.ContainsText("Mouse:"));
                var doctor = FindTextOnLineWith(help, "Doctor", "Close");
                await automator.ClickAtAsync(doctor.X + 1, doctor.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Doctor and runtime capabilities", TimeSpan.FromSeconds(3));
            using (var doctor = automator.CreateSnapshot())
            {
                Assert.IsTrue(doctor.ContainsText("Runtime identifier:"));
                Assert.IsTrue(doctor.ContainsText("Native AOT:"));
                Assert.IsTrue(doctor.ContainsText("Git: 2.50.0"));
                var title = FindText(doctor, "Doctor and runtime capabilities");
                await automator.ClickAtAsync(3, title.Y + 1, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Doctor and runtime capabilities") &&
                    snapshot.ContainsText("Help and keyboard reference"),
                TimeSpan.FromSeconds(3),
                "Doctor closes back to context help");
            using (var help = automator.CreateSnapshot())
            {
                var close = FindTextOnLineWith(help, "Close", "Doctor");
                await automator.ClickAtAsync(close.X + 1, close.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Help and keyboard reference"),
                TimeSpan.FromSeconds(3),
                "The context-help close action is pointer reachable");

            await automator.KeyAsync(Hex1bKey.F2, timeout.Token);
            await automator.WaitUntilTextAsync("Command palette", TimeSpan.FromSeconds(3));
            using (var palette = automator.CreateSnapshot())
            {
                Assert.IsTrue(palette.ContainsText("Help: Context help"));
                Assert.IsTrue(palette.ContainsText("[F1]"));
                var filter = FindText(palette, "Find action:");
                await automator.ClickAtAsync(filter.X + 14, filter.Y, MouseButton.Left, timeout.Token);
            }

            await automator.TypeAsync("abort merge", timeout.Token);
            await automator.WaitUntilTextAsync("Merge: Abort merge", TimeSpan.FromSeconds(3));
            await automator.WaitUntilTextAsync(
                "Unavailable: No verified active merge can be aborted.",
                TimeSpan.FromSeconds(3));
            await new Hex1bTerminalInputSequenceBuilder()
                .Ctrl()
                .Key(Hex1bKey.A)
                .Build()
                .ApplyAsync(terminal, timeout.Token);
            await automator.TypeAsync("stashes", timeout.Token);
            await automator.WaitUntilTextAsync("Stash: Stashes and exact patches", TimeSpan.FromSeconds(3));
            await automator.KeyAsync(Hex1bKey.Enter, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.LoadStashesCallCount == 1,
                TimeSpan.FromSeconds(3),
                "Submitting the palette filter runs its exact focused command");
            await automator.WaitUntilTextAsync("Stashes and exact patches", TimeSpan.FromSeconds(3));

            Assert.AreEqual(1, session.LoadStashesCallCount);
            using (var stashWindow = automator.CreateSnapshot())
            {
                Assert.IsTrue(stashWindow.ContainsText("palette target"));
                var cancel = FindText(stashWindow, "Cancel");
                await automator.ClickAtAsync(cancel.X + 1, cancel.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Stashes and exact patches"),
                TimeSpan.FromSeconds(3),
                "The command-launched stash workspace remains pointer closable");
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

    private static BranchInfo CreateBranch(string fullName, BranchKind kind, bool isCurrent)
    {
        ReadOnlySpan<byte> prefix = kind == BranchKind.Local ? "refs/heads/"u8 : "refs/remotes/"u8;
        var fullNameBytes = System.Text.Encoding.UTF8.GetBytes(fullName);
        Assert.IsTrue(fullNameBytes.AsSpan().StartsWith(prefix));
        Assert.IsTrue(ObjectId.TryParseHex(
            "1111111111111111111111111111111111111111"u8,
            out var objectId));
        return new BranchInfo(
            RefName.FromBytes(fullNameBytes),
            RefName.FromBytes(fullNameBytes.AsSpan(prefix.Length)),
            kind,
            objectId!,
            upstreamName: null,
            aheadCount: 0,
            behindCount: 0,
            isUpstreamGone: false,
            isCurrent,
            isCurrent
                ? [OperatingSystem.IsWindows()
                    ? GitPath.FromWindowsPath("C:\\repository")
                    : GitPath.FromUnixBytes("/repository"u8)]
                : [],
            symbolicTarget: null);
    }

    private static RemoteInfo CreateRemote(string name, string urlText)
    {
        var url = RemoteUrl.FromText(urlText);
        return new RemoteInfo(RemoteName.FromBytes(System.Text.Encoding.UTF8.GetBytes(name)), [url], [url]);
    }

    private static PushPlan CreatePushPlan(
        RemoteInfo remote,
        PushRelationship relationship,
        bool wouldSetUpstream)
    {
        Assert.IsTrue(ObjectId.TryParseHex(
            "2222222222222222222222222222222222222222"u8,
            out var sourceObjectId));
        Assert.IsTrue(ObjectId.TryParseHex(
            "1111111111111111111111111111111111111111"u8,
            out var expectedObjectId));
        var source = RefName.FromBytes("refs/heads/main"u8);
        var destination = RefName.FromBytes("refs/heads/main"u8);
        var catalog = new RemoteCatalog([remote]);
        var expectation = new PushDestinationExpectation(
            remote.PushUrls.Single(),
            relationship == PushRelationship.New ? null : expectedObjectId,
            relationship,
            commitCount: 3);
        var update = new PushUpdatePlan(
            new PushRefSpec(source, destination),
            sourceObjectId,
            [expectation]);
        return new PushPlan(
            catalog,
            remote,
            [update],
            RefName.FromBytes("refs/remotes/origin/main"u8),
            wouldSetUpstream,
            GitOptionOverride.Configured);
    }

    private static StashInfo CreateStash(int index, char objectDigit, string message)
    {
        var objectText = new string(objectDigit, 40);
        Assert.IsTrue(ObjectId.TryParseHex(System.Text.Encoding.ASCII.GetBytes(objectText), out var objectId));
        return new StashInfo(
            index,
            objectId!,
            System.Text.Encoding.UTF8.GetBytes(message),
            DateTimeOffset.FromUnixTimeSeconds(1700000000 - index));
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

    private static async Task OpenRemoteWorkspaceWithMouseAsync(
        Hex1bTerminalAutomator automator,
        FakeRepositoryWorkspaceSession session,
        int expectedLoadCount,
        CancellationToken cancellationToken)
    {
        await automator.WaitUntilAsync(
            snapshot => snapshot.ContainsText("Git 2.50.0") &&
                snapshot.ContainsText("Unstaged (0)") &&
                !snapshot.ContainsText("Remotes and transport"),
            TimeSpan.FromSeconds(3),
            "The base workspace is active before using its remote header action");
        using (var workspace = automator.CreateSnapshot())
        {
            var remotes = FindTextOnLineWith(workspace, "Remotes", "Git 2.50.0");
            await automator.MouseMoveToAsync(remotes.X + 1, remotes.Y, cancellationToken);
            await automator.ClickAtAsync(
                remotes.X + 1,
                remotes.Y,
                MouseButton.Left,
                cancellationToken);
        }

        await automator.WaitUntilAsync(
            _ => session.LoadRemotesCallCount >= expectedLoadCount,
            TimeSpan.FromSeconds(3),
            "The header remote action loads the complete remote workspace");
        await automator.WaitUntilTextAsync("Remotes and transport", TimeSpan.FromSeconds(3));
    }

    private static async Task OpenRemoteWorkspaceThroughPaletteAsync(
        Hex1bTerminalAutomator automator,
        FakeRepositoryWorkspaceSession session,
        int expectedLoadCount,
        CancellationToken cancellationToken)
    {
        await automator.KeyAsync(Hex1bKey.F2, cancellationToken);
        await automator.WaitUntilTextAsync("Command palette", TimeSpan.FromSeconds(3));
        using (var palette = automator.CreateSnapshot())
        {
            var filter = FindText(palette, "Find action:");
            await automator.ClickAtAsync(filter.X + 14, filter.Y, MouseButton.Left, cancellationToken);
        }

        await automator.TypeAsync("remotes", cancellationToken);
        await automator.WaitUntilTextAsync("Remote: Remotes and transport", TimeSpan.FromSeconds(3));
        await automator.KeyAsync(Hex1bKey.Enter, cancellationToken);
        await automator.WaitUntilAsync(
            _ => session.LoadRemotesCallCount >= expectedLoadCount,
            TimeSpan.FromSeconds(3),
            "The palette opens and loads the complete remote workspace");
        await automator.WaitUntilTextAsync("Remotes and transport", TimeSpan.FromSeconds(3));
    }

    private static async Task OpenPushDialogWithMouseAsync(
        Hex1bTerminalAutomator automator,
        CancellationToken cancellationToken)
    {
        using (var remotes = automator.CreateSnapshot())
        {
            var push = FindText(remotes, "Push...");
            await automator.ClickAtAsync(push.X + 1, push.Y, MouseButton.Left, cancellationToken);
        }

        await automator.WaitUntilTextAsync("Push exact Git default plan?", TimeSpan.FromSeconds(3));
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

    private static async Task OpenRevertConfirmationAsync(
        Hex1bTerminalAutomator automator,
        CancellationToken cancellationToken)
    {
        using var snapshot = automator.CreateSnapshot();
        var actionPosition = FindText(snapshot, "Revert...");
        await automator.ClickAtAsync(
            actionPosition.X + 1,
            actionPosition.Y,
            MouseButton.Left,
            cancellationToken);
    }

    private static async Task ConfirmRevertScopeAsync(
        Hex1bTerminalAutomator automator,
        string scopeLabel,
        CancellationToken cancellationToken)
    {
        await OpenRevertConfirmationAsync(automator, cancellationToken);
        await automator.WaitUntilTextAsync("Revert worktree changes?", TimeSpan.FromSeconds(3));
        using var confirmation = automator.CreateSnapshot();
        var scopePosition = FindTextOnLineWith(confirmation, scopeLabel, "Cancel");
        await automator.ClickAtAsync(
            scopePosition.X + 1,
            scopePosition.Y,
            MouseButton.Left,
            cancellationToken);
    }

    private static async Task ClickUndoRevertAsync(
        Hex1bTerminalAutomator automator,
        CancellationToken cancellationToken)
    {
        await automator.WaitUntilTextAsync("Undo revert", TimeSpan.FromSeconds(3));
        using var snapshot = automator.CreateSnapshot();
        var undoPosition = FindText(snapshot, "Undo revert");
        await automator.ClickAtAsync(
            undoPosition.X + 1,
            undoPosition.Y,
            MouseButton.Left,
            cancellationToken);
    }
}
