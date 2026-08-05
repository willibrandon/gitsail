using GitSail.CommandLine;
using GitSail.Diagnostics;
using GitSail.Domain;
using GitSail.Git.Execution;
using GitSail.Ui;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;
using Hex1b.Theming;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies first-class pointer interaction against the real headless workspace widget tree.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RepositoryWorkspaceViewMouseTests
{
    /// <summary>
    /// Verifies repository statistics and every care confirmation remain readable and mouse-operable.
    /// </summary>
    [TestMethod]
    public async Task RepositoryCare_ThroughCommandPalette_ShowsCountsAndCancelFirstMouseActions()
    {
        var session = new FakeRepositoryWorkspaceSession();
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
            await automator.WaitUntilTextAsync("Git 2.50.0", TimeSpan.FromSeconds(3));
            await automator.KeyAsync(Hex1bKey.F2, timeout.Token);
            await automator.WaitUntilTextAsync("Command palette", TimeSpan.FromSeconds(3));
            using (var palette = automator.CreateSnapshot())
            {
                var filter = FindText(palette, "Find action:");
                await automator.ClickAtAsync(
                    filter.X + 14,
                    filter.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.TypeAsync("repository statistics", timeout.Token);
            await automator.WaitUntilTextAsync(
                "Repository: Repository statistics",
                TimeSpan.FromSeconds(3));
            await automator.KeyAsync(Hex1bKey.Enter, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => session.LoadRepositoryStatisticsCallCount == 1 &&
                    snapshot.ContainsText("Repository statistics and maintenance") &&
                    snapshot.ContainsText("Loose objects: 12") &&
                    snapshot.ContainsText("Packed objects: 345") &&
                    snapshot.ContainsText("Alternate databases: 2"),
                TimeSpan.FromSeconds(3),
                "The palette opens complete repository statistics");

            using (var statistics = automator.CreateSnapshot())
            {
                AssertWindowFrameIsComplete(
                    statistics,
                    "Repository statistics and maintenance",
                    78,
                    22);
                var maintenance = FindText(statistics, "Run maintenance...");
                await automator.ClickAtAsync(
                    maintenance.X + 2,
                    maintenance.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilTextAsync("git maintenance run", TimeSpan.FromSeconds(3));
            using (var confirmation = automator.CreateSnapshot())
            {
                AssertWindowFrameIsComplete(confirmation, "Run configured maintenance?", 78, 12);
            }

            await automator.KeyAsync(Hex1bKey.Escape, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("git maintenance run") &&
                    snapshot.ContainsText("Repository statistics and maintenance"),
                TimeSpan.FromSeconds(3),
                "Escape cancels only the nested maintenance confirmation");
            Assert.AreEqual(0, session.RunConfiguredMaintenanceCallCount);

            using (var statistics = automator.CreateSnapshot())
            {
                var garbageCollect = FindText(statistics, "Garbage collect...");
                await automator.ClickAtAsync(
                    garbageCollect.X + 2,
                    garbageCollect.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilTextAsync("git gc --no-detach", TimeSpan.FromSeconds(3));
            using (var confirmation = automator.CreateSnapshot())
            {
                AssertWindowFrameIsComplete(confirmation, "Run garbage collection?", 78, 12);
            }

            await automator.KeyAsync(Hex1bKey.Escape, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("git gc --no-detach") &&
                    snapshot.ContainsText("Repository statistics and maintenance"),
                TimeSpan.FromSeconds(3),
                "Escape cancels only the nested garbage-collection confirmation");

            using (var statistics = automator.CreateSnapshot())
            {
                var verify = FindText(statistics, "Verify objects...");
                await automator.ClickAtAsync(
                    verify.X + 2,
                    verify.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilTextAsync("git fsck --full --no-progress", TimeSpan.FromSeconds(3));
            using (var confirmation = automator.CreateSnapshot())
            {
                AssertWindowFrameIsComplete(confirmation, "Verify repository integrity?", 78, 12);
                var verify = FindTextOnLineWith(confirmation, "Run verification", "Cancel");
                await automator.ClickAtAsync(
                    verify.X + 2,
                    verify.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilAsync(
                snapshot => session.VerifyRepositoryCallCount == 1 &&
                    snapshot.ContainsText("Repository verification:") &&
                    snapshot.ContainsText("verification complete"),
                TimeSpan.FromSeconds(3),
                "The mouse-confirmed verification publishes exact output");
            await automator.ClickAtAsync(0, 0, MouseButton.Left, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Repository statistics and maintenance"),
                TimeSpan.FromSeconds(3),
                "Clicking outside closes the top-level repository-care window");
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies merge mode is a complete mouse-operable conflict workspace at 60 by 18.
    /// </summary>
    [TestMethod]
    public async Task MergeMode_AtMinimumSize_ShowsOnlyConflictActionsAndSupportsMouseResolution()
    {
        var session = new FakeRepositoryWorkspaceSession(
            FakeRepositoryWorkspaceSession.CreateUnmergedEntry("conflict.txt"));
        session.ConfigureConflict(chunkCount: 1);
        var view = new RepositoryWorkspaceView(
            new GitSailShellOptions(ApplicationMode.Merge, WorkingDirectory: null),
            session,
            CancellationToken.None);
        Hex1bApp? application = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(60, 18)
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
            await automator.WaitUntilTextAsync("Unmerged (1)", TimeSpan.FromSeconds(3));
            using (var snapshot = automator.CreateSnapshot())
            {
                Assert.IsTrue(snapshot.ContainsText("Git 2.50.0"));
                Assert.IsTrue(snapshot.ContainsText("Conflict: conflict.txt"));
                Assert.IsTrue(snapshot.ContainsText("O+T"));
                Assert.IsTrue(snapshot.ContainsText("Ctrl+Q Quit"));
                Assert.IsFalse(snapshot.ContainsText("Commit message"));
                Assert.IsFalse(snapshot.ContainsText("Commands"));
                Assert.IsFalse(snapshot.ContainsText("Branches"));
                Assert.IsFalse(snapshot.ContainsText("Remotes"));
                Assert.IsFalse(snapshot.ContainsText("Stashes"));
                var chooseBoth = FindText(snapshot, "O+T");
                await automator.ClickAtAsync(
                    chooseBoth.X + 1,
                    chooseBoth.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilAsync(
                snapshot => session.LastConflictChoice == ConflictResolutionChoice.Both &&
                    session.CanStageConflictResolution &&
                    snapshot.ContainsText("Done"),
                TimeSpan.FromSeconds(3),
                "The merge result renders the completed pointer-selected conflict choice");
            using (var resolved = automator.CreateSnapshot())
            {
                var stage = FindTextOnLineWith(resolved, "Stage", "Quit");
                await automator.ClickAtAsync(stage.X + 1, stage.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilAsync(
                _ => session.StageConflictResolutionCallCount == 1,
                TimeSpan.FromSeconds(3),
                "The resolved merge result can be staged with the pointer");
            using (var staged = automator.CreateSnapshot())
            {
                var quit = FindText(staged, "Quit");
                await automator.ClickAtAsync(quit.X + 1, quit.Y, MouseButton.Left, timeout.Token);
            }

            await runTask.WaitAsync(timeout.Token);
            Assert.AreEqual(0, session.CommitCallCount);
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies rebase resolution mode replaces every ordinary commit action with a safe return action.
    /// </summary>
    [TestMethod]
    public async Task RebaseMode_WithStagedResolution_ReturnsWithoutCreatingOrdinaryCommit()
    {
        var session = new FakeRepositoryWorkspaceSession(
            FakeRepositoryWorkspaceSession.CreateStagedEntry("resolved.txt"));
        var view = new RepositoryWorkspaceView(
            new GitSailShellOptions(ApplicationMode.Rebase, WorkingDirectory: null),
            session,
            CancellationToken.None);
        Hex1bApp? application = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(100, 30)
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
            await automator.WaitUntilTextAsync("F4 Return to rebase", TimeSpan.FromSeconds(3));
            using (var snapshot = automator.CreateSnapshot())
            {
                Assert.IsTrue(snapshot.ContainsText("Return to rebase"));
                Assert.IsTrue(snapshot.ContainsText("Resolve and stage files"));
                Assert.IsFalse(snapshot.ContainsText("F4 Commit"));
                Assert.IsFalse(snapshot.ContainsText("Commit unavailable"));
                Assert.IsFalse(snapshot.ContainsText("Commands"));
                Assert.IsFalse(snapshot.ContainsText("Branches"));
                Assert.IsFalse(snapshot.ContainsText("Remotes"));
                Assert.IsFalse(snapshot.ContainsText("Stashes"));
            }

            await automator.KeyAsync(Hex1bKey.F4, timeout.Token);
            await runTask.WaitAsync(timeout.Token);
            Assert.AreEqual(0, session.CommitCallCount);
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies a complete Windows Git version remains readable beside every header action.
    /// </summary>
    [TestMethod]
    public async Task Header_WithWindowsGitVersion_ShowsCompleteVersionAndActions()
    {
        var session = new FakeRepositoryWorkspaceSession("git version 2.51.1.windows.1");
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
            await automator.WaitUntilTextAsync("Git 2.51.1.windows.1", TimeSpan.FromSeconds(3));
            using var snapshot = automator.CreateSnapshot();
            var version = FindText(snapshot, "Git 2.51.1.windows.1");
            var commands = FindText(snapshot, "Commands");
            var branches = FindText(snapshot, "Branches");
            var remotes = FindText(snapshot, "Remotes");
            var stashes = FindText(snapshot, "Stashes");

            Assert.IsLessThan(commands.Y, version.Y);
            Assert.AreEqual(commands.Y, branches.Y);
            Assert.AreEqual(branches.Y, remotes.Y);
            Assert.AreEqual(remotes.Y, stashes.Y);
            Assert.IsLessThanOrEqualTo(snapshot.Width, version.X + "Git 2.51.1.windows.1".Length);
            Assert.IsLessThanOrEqualTo(snapshot.Width, stashes.X + "Stashes".Length);
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies the supported minimum uses mouse-operable tabs and F6 cycles every workspace region.
    /// </summary>
    [TestMethod]
    public async Task Workspace_AtSixtyByEighteen_UsesTabsAndCyclesEveryRegion()
    {
        var session = new FakeRepositoryWorkspaceSession(
            FakeRepositoryWorkspaceSession.CreateUnstagedEntry("narrow.txt"),
            FakeRepositoryWorkspaceSession.CreateStagedEntry("indexed.txt"));
        var view = new RepositoryWorkspaceView(
            new GitSailShellOptions(ApplicationMode.Gui, WorkingDirectory: null),
            session,
            CancellationToken.None);
        Hex1bApp? application = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(60, 18)
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
            await automator.WaitUntilTextAsync("Unstaged (1)", TimeSpan.FromSeconds(3));
            using (var changes = automator.CreateSnapshot())
            {
                Assert.IsFalse(changes.ContainsText("Terminal too small"));
                Assert.IsTrue(changes.ContainsText("Changes"));
                Assert.IsTrue(changes.ContainsText("Diff"));
                Assert.IsTrue(changes.ContainsText("Commit"));
                Assert.IsTrue(changes.ContainsText("Ctrl+Q Quit"));
                var diffTab = FindText(changes, "Diff");
                await automator.ClickAtAsync(
                    diffTab.X + 1,
                    diffTab.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilTextAsync("Unstaged: narrow.txt", TimeSpan.FromSeconds(3));
            using (var diff = automator.CreateSnapshot())
            {
                Assert.IsTrue(diff.ContainsText("diff --git a/narrow.txt b/narrow.txt"));
                var commitTab = FindText(diff, "Commit");
                await automator.ClickAtAsync(
                    commitTab.X + 1,
                    commitTab.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilTextAsync("Commit message", TimeSpan.FromSeconds(3));
            using (var commit = automator.CreateSnapshot())
            {
                var title = FindText(commit, "Commit message");
                await automator.ClickAtAsync(4, title.Y + 2, MouseButton.Left, timeout.Token);
            }

            await automator.TypeAsync("minimum layout", timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.CommitMessage.Message.Contains("minimum layout", StringComparison.Ordinal),
                TimeSpan.FromSeconds(3),
                "The minimum-size commit tab remains editable");
            await automator.KeyAsync(Hex1bKey.F6, timeout.Token);
            await automator.WaitUntilTextAsync("Unstaged (1)", TimeSpan.FromSeconds(3));
            await automator.KeyAsync(Hex1bKey.F6, timeout.Token);
            await automator.WaitUntilTextAsync("Unstaged: narrow.txt", TimeSpan.FromSeconds(3));
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies an 80-column workspace stacks changes above diff and commit without hiding actions.
    /// </summary>
    [TestMethod]
    public async Task Workspace_AtEightyByTwentyFour_StacksEveryRegionWithoutOverflow()
    {
        var session = new FakeRepositoryWorkspaceSession(
            FakeRepositoryWorkspaceSession.CreateUnstagedEntry("medium.txt"));
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
            await automator.WaitUntilTextAsync("Commit message", TimeSpan.FromSeconds(3));
            using var snapshot = automator.CreateSnapshot();
            var changes = FindText(snapshot, "Unstaged (1)");
            var diff = FindText(snapshot, "Unstaged: medium.txt");
            var commit = FindText(snapshot, "Commit message");
            var actions = FindText(snapshot, "Refresh");

            Assert.IsLessThan(diff.Y, changes.Y);
            Assert.IsLessThan(commit.Y, diff.Y);
            Assert.IsLessThan(actions.Y, commit.Y);
            Assert.IsTrue(snapshot.ContainsText("F2 Commands"));
            Assert.IsTrue(snapshot.ContainsText("Ctrl+Q Quit"));
            Assert.IsFalse(snapshot.ContainsText("Terminal too small"));
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies a clean repository presents a calm action row without repeated disabled-action labels.
    /// </summary>
    [TestMethod]
    public async Task Workspace_WithCleanWorkingTree_ShowsOnlyUsefulActions()
    {
        var session = new FakeRepositoryWorkspaceSession();
        var view = new RepositoryWorkspaceView(
            new GitSailShellOptions(ApplicationMode.Gui, WorkingDirectory: null),
            session,
            CancellationToken.None);
        Hex1bApp? application = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(200, 40)
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
            await automator.WaitUntilTextAsync("Working tree clean", TimeSpan.FromSeconds(3));
            using (var snapshot = automator.CreateSnapshot())
            {
                Assert.IsFalse(snapshot.ContainsText("unavailable"));
                Assert.IsTrue(snapshot.ContainsText("No staged changes."));
                var refresh = FindTextOnLineWith(snapshot, "Refresh", "Working tree clean");
                await automator.ClickAtAsync(
                    refresh.X + 1,
                    refresh.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilAsync(
                _ => session.RefreshCallCount == 1,
                TimeSpan.FromSeconds(3),
                "The clean-state refresh action remains mouse-activatable");
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies F7 opens and focuses the shared path filter in the compact tabbed workspace.
    /// </summary>
    [TestMethod]
    public async Task Workspace_F7PathFilter_FiltersChangedPathsAndLoadsMatchingDiff()
    {
        var session = new FakeRepositoryWorkspaceSession(
            FakeRepositoryWorkspaceSession.CreateUnstagedEntry("alpha.txt"),
            FakeRepositoryWorkspaceSession.CreateUnstagedEntry("beta.txt"));
        var view = new RepositoryWorkspaceView(
            new GitSailShellOptions(ApplicationMode.Gui, WorkingDirectory: null),
            session,
            CancellationToken.None);
        Hex1bApp? application = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(70, 24)
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
            await automator.WaitUntilTextAsync("Unstaged (2)", TimeSpan.FromSeconds(3));
            await automator.KeyAsync(Hex1bKey.F6, timeout.Token);
            await automator.WaitUntilTextAsync("Unstaged: alpha.txt", TimeSpan.FromSeconds(3));

            await automator.KeyAsync(Hex1bKey.F7, timeout.Token);
            await automator.WaitUntilTextAsync("Find:", TimeSpan.FromSeconds(3));
            await automator.TypeAsync("beta", timeout.Token);

            await automator.WaitUntilAsync(
                snapshot => snapshot.ContainsText("Unstaged (1/2)") &&
                    snapshot.ContainsText("beta.txt") &&
                    string.Equals(session.State.Filter.Text, "beta", StringComparison.Ordinal),
                TimeSpan.FromSeconds(3),
                "F7 focuses the changed-path filter and publishes its matching row");
            Assert.AreEqual("beta.txt", session.State.FocusedItem?.Path.DisplayText);
            Assert.IsTrue(session.Diff.Title.Contains("beta.txt", StringComparison.Ordinal));

            await automator.KeyAsync(Hex1bKey.A, Hex1bModifiers.Control, timeout.Token);
            await automator.TypeAsync("no-match", timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => snapshot.ContainsText("Unstaged (0/2)") &&
                    !snapshot.ContainsText("Working tree clean"),
                TimeSpan.FromSeconds(3),
                "An empty filtered view remains distinct from a clean repository");

            await automator.KeyAsync(Hex1bKey.F2, timeout.Token);
            await automator.WaitUntilTextAsync("Command palette", TimeSpan.FromSeconds(3));
            await automator.TypeAsync("stage all", timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => snapshot.ContainsText("Commit: Stage all") &&
                    snapshot.ContainsText("Available now"),
                TimeSpan.FromSeconds(3),
                "Stage all remains available because filtering is presentation-only");
            await automator.KeyAsync(Hex1bKey.Enter, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.StageAllCallCount == 1,
                TimeSpan.FromSeconds(3),
                "Stage all executes against the complete repository despite the empty filter result");
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies Ctrl+F and pointer controls traverse case-insensitive diff matches in compact layout.
    /// </summary>
    [TestMethod]
    public async Task Workspace_DiffTextSearch_UsesKeyboardAndMouseNavigation()
    {
        var session = new FakeRepositoryWorkspaceSession(
            FakeRepositoryWorkspaceSession.CreateUnstagedEntry("search.txt"));
        var view = new RepositoryWorkspaceView(
            new GitSailShellOptions(ApplicationMode.Gui, WorkingDirectory: null),
            session,
            CancellationToken.None);
        Hex1bApp? application = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(70, 24)
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
            await automator.WaitUntilTextAsync("Unstaged (1)", TimeSpan.FromSeconds(3));
            await automator.KeyAsync(Hex1bKey.F, Hex1bModifiers.Control, timeout.Token);
            await automator.WaitUntilTextAsync("Text:", TimeSpan.FromSeconds(3));
            await automator.TypeAsync("NEW LINE", timeout.Token);
            await automator.KeyAsync(Hex1bKey.Enter, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => snapshot.ContainsText("1/20") && snapshot.ContainsText("+new line 2"),
                TimeSpan.FromSeconds(3),
                "Ctrl+F selects and reveals the first case-insensitive diff match");
            await automator.WaitUntilAsync(
                snapshot => !snapshot.GetLine(snapshot.CursorY).Contains("Text:", StringComparison.Ordinal),
                TimeSpan.FromSeconds(3),
                "Search submission moves focus to the selected diff match");
            Assert.AreEqual(
                "new line",
                session.Diff.Editor.Document.GetText(session.Diff.Editor.Cursor.SelectionRange),
                ignoreCase: true);

            await automator.KeyAsync(Hex1bKey.F, Hex1bModifiers.Control, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => snapshot.GetLine(snapshot.CursorY).Contains("Text:", StringComparison.Ordinal) &&
                    snapshot.CursorX < 50,
                TimeSpan.FromSeconds(3),
                "Ctrl+F returns focus from the diff editor to the search field");
            await automator.KeyAsync(Hex1bKey.A, Hex1bModifiers.Control, timeout.Token);
            await automator.TypeAsync("OLD LINE", timeout.Token);
            await automator.KeyAsync(Hex1bKey.Enter, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => snapshot.ContainsText("2/20") &&
                    string.Equals(session.Diff.Search.Text, "OLD LINE", StringComparison.Ordinal),
                TimeSpan.FromSeconds(3),
                "Ctrl+F replaces the active query from the diff editor");

            await automator.KeyAsync(Hex1bKey.F3, timeout.Token);
            await automator.WaitUntilTextAsync("3/20", TimeSpan.FromSeconds(3));
            await automator.KeyAsync(Hex1bKey.F3, Hex1bModifiers.Shift, timeout.Token);
            await automator.WaitUntilTextAsync("2/20", TimeSpan.FromSeconds(3));

            using (var snapshot = automator.CreateSnapshot())
            {
                var next = FindTextOnLineWith(snapshot, "Next", "Text:");
                await automator.ClickAtAsync(next.X + 1, next.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("3/20", TimeSpan.FromSeconds(3));
            Assert.AreEqual(
                "old line",
                session.Diff.Editor.Document.GetText(session.Diff.Editor.Cursor.SelectionRange),
                ignoreCase: true);

            using (var snapshot = automator.CreateSnapshot())
            {
                var hide = FindTextOnLineWith(snapshot, "Hide", "Text:");
                await automator.ClickAtAsync(hide.X + 1, hide.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Text:"),
                TimeSpan.FromSeconds(3),
                "The pointer Hide control returns the line to the diff");
            await automator.KeyAsync(Hex1bKey.F, Hex1bModifiers.Control, timeout.Token);
            await automator.WaitUntilTextAsync("Text: OLD LINE", TimeSpan.FromSeconds(3));
            await automator.KeyAsync(Hex1bKey.Escape, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Text:"),
                TimeSpan.FromSeconds(3),
                "Escape hides diff search while retaining its query");
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies the wide menu bar exposes every top-level menu and executes its shared live action.
    /// </summary>
    [TestMethod]
    public async Task MenuBar_AtOneHundredTwentyColumns_ShowsEveryMenuAndRunsPointerAction()
    {
        var session = new FakeRepositoryWorkspaceSession(
            FakeRepositoryWorkspaceSession.CreateUnstagedEntry("menu.txt"));
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
            await automator.WaitUntilTextAsync("Commit message", TimeSpan.FromSeconds(3));
            using (var workspace = automator.CreateSnapshot())
            {
                var repository = FindText(workspace, "Repository");
                var edit = FindTextOnLineWith(workspace, "Edit", "Repository");
                var viewMenu = FindTextOnLineWith(workspace, "View", "Repository");
                var branch = FindTextOnLineWith(workspace, "Branch", "Repository");
                var commit = FindTextOnLineWith(workspace, "Commit", "Repository");
                var merge = FindTextOnLineWith(workspace, "Merge", "Repository");
                var remote = FindTextOnLineWith(workspace, "Remote", "Repository");
                var stash = FindTextOnLineWith(workspace, "Stash", "Repository");
                var history = FindTextOnLineWith(workspace, "History", "Repository");
                var tools = FindTextOnLineWith(workspace, "Tools", "Repository");
                var help = FindTextOnLineWith(workspace, "Help", "Repository");

                Assert.AreEqual(repository.Y, edit.Y);
                Assert.AreEqual(repository.Y, viewMenu.Y);
                Assert.AreEqual(repository.Y, branch.Y);
                Assert.AreEqual(repository.Y, commit.Y);
                Assert.AreEqual(repository.Y, merge.Y);
                Assert.AreEqual(repository.Y, remote.Y);
                Assert.AreEqual(repository.Y, stash.Y);
                Assert.AreEqual(repository.Y, history.Y);
                Assert.AreEqual(repository.Y, tools.Y);
                Assert.AreEqual(repository.Y, help.Y);
                await automator.ClickAtAsync(
                    viewMenu.X + 1,
                    viewMenu.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilTextAsync("Decrease diff context", TimeSpan.FromSeconds(3));
            using (var menu = automator.CreateSnapshot())
            {
                var decrease = FindText(menu, "Decrease diff context");
                await automator.ClickAtAsync(
                    decrease.X + 1,
                    decrease.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilAsync(
                _ => session.DecreaseDiffContextCallCount == 1,
                TimeSpan.FromSeconds(3),
                "The View menu executes the same decrease-context action as the palette and key binding");
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Decrease diff context"),
                TimeSpan.FromSeconds(3),
                "The menu closes after its action runs");
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies F10 opens every menu at compact widths and history returns through the shell request.
    /// </summary>
    [TestMethod]
    public async Task ApplicationMenu_AtEightyColumns_OpensWithF10AndRunsHistory()
    {
        var session = new FakeRepositoryWorkspaceSession();
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
            await automator.WaitUntilTextAsync("Commit message", TimeSpan.FromSeconds(3));
            using (var workspace = automator.CreateSnapshot())
            {
                var menuButton = FindTextOnLineWith(workspace, "Menu", "Commands");
                await automator.ClickAtAsync(
                    menuButton.X + 1,
                    menuButton.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilTextAsync("GitSail menu", TimeSpan.FromSeconds(3));
            using (var menu = automator.CreateSnapshot())
            {
                var repository = FindText(menu, "> Repository");
                await automator.ClickAtAsync(
                    repository.X + 2,
                    repository.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.KeyAsync(Hex1bKey.Escape, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("GitSail menu"),
                TimeSpan.FromSeconds(3),
                "One Escape closes the compact pointer menu while its category list owns focus");
            await automator.KeyAsync(Hex1bKey.F10, timeout.Token);
            await automator.WaitUntilTextAsync("GitSail menu", TimeSpan.FromSeconds(3));
            await automator.ClickAtAsync(0, 23, MouseButton.Left, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("GitSail menu"),
                TimeSpan.FromSeconds(3),
                "Clicking outside closes the complete application menu");
            await automator.KeyAsync(Hex1bKey.F10, timeout.Token);
            await automator.WaitUntilTextAsync("GitSail menu", TimeSpan.FromSeconds(3));
            using (var menu = automator.CreateSnapshot())
            {
                AssertWindowFrameIsComplete(menu, "GitSail menu", 58, 16);
                Assert.IsTrue(menu.ContainsText("Repository"));
                Assert.IsTrue(menu.ContainsText("Edit"));
                Assert.IsTrue(menu.ContainsText("Esc/click outside closes"));
                var repository = FindText(menu, "> Repository");
                await automator.ClickAtAsync(
                    repository.X + 2,
                    repository.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.ScrollDownAsync(8, timeout.Token);
            await automator.WaitUntilTextAsync("History", TimeSpan.FromSeconds(3));
            using (var menu = automator.CreateSnapshot())
            {
                Assert.IsTrue(menu.ContainsText("History"));
                var history = FindText(menu, "History");
                await automator.ClickAtAsync(
                    history.X + 1,
                    history.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilTextAsync("Repository history", TimeSpan.FromSeconds(3));
            using (var historyMenu = automator.CreateSnapshot())
            {
                var historyAction = FindText(historyMenu, "Repository history");
                await automator.DoubleClickAtAsync(
                    historyAction.X + 1,
                    historyAction.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilAsync(
                _ => session.RequestedDestination == RepositoryWorkspaceDestination.History,
                TimeSpan.FromSeconds(3),
                "The History action requests the real history workspace");
            await runTask;
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies the application menu remains framed, scrollable, and dismissible after resizing to the supported minimum.
    /// </summary>
    [TestMethod]
    public async Task ApplicationMenu_AfterResizeToSixtyByEighteen_ReachesEveryCategoryAndCloses()
    {
        var session = new FakeRepositoryWorkspaceSession();
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
            await automator.KeyAsync(Hex1bKey.F10, timeout.Token);
            await automator.WaitUntilTextAsync("GitSail menu", TimeSpan.FromSeconds(3));
            terminal.Resize(60, 18);
            await automator.WaitUntilAsync(
                snapshot => snapshot.Width == 60 &&
                    snapshot.Height == 18 &&
                    snapshot.ContainsText("Esc/click outside closes"),
                TimeSpan.FromSeconds(3),
                "The application menu completes its minimum-size frame after resize");

            using (var menu = automator.CreateSnapshot())
            {
                AssertWindowFrameIsComplete(menu, "GitSail menu", 58, 16);
                Assert.IsTrue(menu.ContainsText("Repository"));
                Assert.IsTrue(menu.ContainsText("Commit"));
                var repository = FindText(menu, "> Repository");
                await automator.ClickAtAsync(
                    repository.X + 2,
                    repository.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.ScrollDownAsync(20, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => snapshot.ContainsText("History") &&
                    snapshot.ContainsText("Tools") &&
                    snapshot.ContainsText("Help"),
                TimeSpan.FromSeconds(3),
                "Scrolling the compact category list reaches its final categories");
            await automator.KeyAsync(Hex1bKey.Escape, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("GitSail menu"),
                TimeSpan.FromSeconds(3),
                "Escape closes the application menu after resize and scrolling");
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies Edit menu actions retain the active editor and mutate the same shared editor state.
    /// </summary>
    [TestMethod]
    public async Task ApplicationMenu_FromCommitEditor_CutsTheSelectedMessage()
    {
        var session = new FakeRepositoryWorkspaceSession();
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
            await automator.WaitUntilTextAsync("Commit message", TimeSpan.FromSeconds(3));
            using (var workspace = automator.CreateSnapshot())
            {
                var commit = FindText(workspace, "Commit message");
                await automator.ClickAtAsync(4, commit.Y + 2, MouseButton.Left, timeout.Token);
            }

            await automator.TypeAsync("selected commit message", timeout.Token);
            await new Hex1bTerminalInputSequenceBuilder()
                .Ctrl()
                .Key(Hex1bKey.A)
                .Build()
                .ApplyAsync(terminal, timeout.Token);
            await automator.KeyAsync(Hex1bKey.F10, timeout.Token);
            await automator.WaitUntilTextAsync("GitSail menu", TimeSpan.FromSeconds(3));
            using (var menu = automator.CreateSnapshot())
            {
                var edit = FindText(menu, "Edit");
                await automator.ClickAtAsync(edit.X + 1, edit.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Edit: Cut", TimeSpan.FromSeconds(3));
            using (var editMenu = automator.CreateSnapshot())
            {
                var cut = FindText(editMenu, "Edit: Cut");
                await automator.DoubleClickAtAsync(cut.X + 1, cut.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilAsync(
                _ => session.CommitMessage.Message.Length == 0,
                TimeSpan.FromSeconds(3),
                "Cut removes the selection from the same commit editor opened before F10");
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies the primary diff pane receives more rows than the commit editor in a tall workspace.
    /// </summary>
    [TestMethod]
    public async Task DetailLayout_AtFortyRows_GivesDiffMoreRowsThanCommitMessage()
    {
        var session = new FakeRepositoryWorkspaceSession(
            FakeRepositoryWorkspaceSession.CreateUnstagedEntry("sample.txt"));
        var view = new RepositoryWorkspaceView(
            new GitSailShellOptions(ApplicationMode.Gui, WorkingDirectory: null),
            session,
            CancellationToken.None);
        Hex1bApp? application = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(120, 40)
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
            using var snapshot = automator.CreateSnapshot();
            var diffTitle = FindText(snapshot, session.Diff.Title);
            var commitTitle = FindText(snapshot, "Commit message");
            var shortcutBar = FindText(snapshot, "F4 Commit");
            var diffRows = commitTitle.Y - diffTitle.Y - 1;
            var commitRows = shortcutBar.Y - commitTitle.Y - 1;

            Assert.IsGreaterThan(
                commitRows,
                diffRows,
                $"The diff received {diffRows} rows while the commit section received {commitRows} rows.");
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies a roomy terminal presents every shortcut group on complete bounded rows.
    /// </summary>
    [TestMethod]
    public async Task ShortcutBar_AtRoomySize_ShowsCompleteRowsWithoutOverflow()
    {
        var session = new FakeRepositoryWorkspaceSession(
            FakeRepositoryWorkspaceSession.CreateUnstagedEntry("sample.txt"));
        var view = new RepositoryWorkspaceView(
            new GitSailShellOptions(ApplicationMode.Gui, WorkingDirectory: null),
            session,
            CancellationToken.None);
        Hex1bApp? application = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(196, 40)
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
            await automator.WaitUntilTextAsync("P Prepare hunks", TimeSpan.FromSeconds(3));
            using var snapshot = automator.CreateSnapshot();
            var global = FindText(snapshot, "F4 Commit");
            var changes = FindText(snapshot, "P Prepare hunks");
            var diff = FindText(snapshot, "Mouse Diff");

            Assert.IsLessThan(changes.Y, global.Y);
            Assert.IsLessThan(diff.Y, changes.Y);
            Assert.IsTrue(snapshot.ContainsText("Ctrl+Q Quit"));
            Assert.IsTrue(snapshot.ContainsText("Shift+U Unstage all"));
            Assert.IsTrue(snapshot.ContainsText("Ctrl+Z Undo revert"));
            Assert.IsLessThanOrEqualTo(snapshot.Width, changes.X + "P Prepare hunks".Length);
            Assert.IsLessThanOrEqualTo(snapshot.Width, diff.X + "Mouse Diff".Length);
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies replacement diff documents retain green styling through every added-line cell at 80 by 24.
    /// </summary>
    [TestMethod]
    public async Task DiffRendering_AfterDocumentReplacement_ColorsCompleteAddedLine()
    {
        var session = new FakeRepositoryWorkspaceSession(
            FakeRepositoryWorkspaceSession.CreateUnstagedEntry("short.txt"));
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
        const string addedLine = "+this added line stays green";

        try
        {
            await automator.WaitUntilTextAsync("@@ -1,20 +1,20 @@", TimeSpan.FromSeconds(3));
            session.ConfigureDiff(
                "Unstaged: longer.txt",
                "diff --git a/longer.txt b/longer.txt\n" +
                "--- a/longer.txt\n" +
                "+++ b/longer.txt\n" +
                "@@ -1 +1 @@\n" +
                addedLine + "\n");
            await automator.WaitUntilTextAsync(addedLine, TimeSpan.FromSeconds(3));

            using var snapshot = automator.CreateSnapshot();
            Assert.IsTrue(snapshot.ContainsText("Commit message"));
            var position = FindText(snapshot, addedLine);
            var expectedForeground = Hex1bColor.FromRgb(80, 220, 80);
            var expectedBackground = Hex1bColor.FromRgb(20, 40, 20);
            for (var offset = 0; offset < addedLine.Length; offset++)
            {
                var cell = snapshot.GetCell(position.X + offset, position.Y);
                Assert.AreEqual(
                    expectedForeground,
                    cell.Foreground,
                    $"Added-line foreground stopped at character {offset}.");
                Assert.AreEqual(
                    expectedBackground,
                    cell.Background,
                    $"Added-line background stopped at character {offset}.");
            }
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

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
            .WithDimensions(320, 30)
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
            using (var workspace = automator.CreateSnapshot())
            {
                var secondFile = FindText(workspace, "file-01.txt");
                await automator.ClickAtAsync(
                    secondFile.X + 1,
                    secondFile.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

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

            (int X, int Y) firstSelectionRow;
            (int X, int Y) rangeSelectionRow;
            using (var workspace = automator.CreateSnapshot())
            {
                firstSelectionRow = FindText(workspace, "file-00.txt");
                rangeSelectionRow = FindText(workspace, "file-03.txt");
            }

            await new Hex1bTerminalInputSequenceBuilder()
                .Ctrl()
                .ClickAt(firstSelectionRow.X + 1, firstSelectionRow.Y, MouseButton.Left)
                .Build()
                .ApplyAsync(terminal, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.State.UnstagedSelectedIndices.Count > 0,
                TimeSpan.FromSeconds(3),
                "Ctrl-click checks a row");

            await new Hex1bTerminalInputSequenceBuilder()
                .Shift()
                .ClickAt(rangeSelectionRow.X + 1, rangeSelectionRow.Y, MouseButton.Left)
                .Build()
                .ApplyAsync(terminal, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.State.UnstagedSelectedIndices.Count > 1,
                TimeSpan.FromSeconds(3),
                "Shift-click extends a checked range");

            var focusedIndexBeforeWheel = session.State.UnstagedFocusedIndex;
            await automator.MouseMoveToAsync(10, 6, timeout.Token);
            await automator.ScrollDownAsync(8, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.State.UnstagedFocusedIndex > focusedIndexBeforeWheel,
                TimeSpan.FromSeconds(3),
                "The wheel advances the worktree list focus");
            var focusedPath = session.State.UnstagedItems[session.State.UnstagedFocusedIndex].Path.DisplayText;
            await automator.WaitUntilTextAsync(focusedPath, TimeSpan.FromSeconds(3));

            await automator.ScrollDownAsync(25, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.State.UnstagedFocusedIndex == session.State.UnstagedItems.Length - 1,
                TimeSpan.FromSeconds(3),
                "The wheel reaches the last worktree row");
            await automator.ScrollDownAsync(4, timeout.Token);
            Assert.AreEqual(
                session.State.UnstagedItems.Length - 1,
                session.State.UnstagedFocusedIndex,
                "Wheel input wrapped from the last worktree row to the first row.");
            await automator.KeyAsync(Hex1bKey.DownArrow, timeout.Token);
            Assert.AreEqual(
                session.State.UnstagedItems.Length - 1,
                session.State.UnstagedFocusedIndex,
                "Down Arrow wrapped from the last worktree row to the first row.");

            await automator.ScrollUpAsync(25, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.State.UnstagedFocusedIndex == 0,
                TimeSpan.FromSeconds(3),
                "The wheel reaches the first worktree row");
            await automator.ScrollUpAsync(4, timeout.Token);
            Assert.AreEqual(
                0,
                session.State.UnstagedFocusedIndex,
                "Wheel input wrapped from the first worktree row to the last row.");
            await automator.KeyAsync(Hex1bKey.UpArrow, timeout.Token);
            Assert.AreEqual(
                0,
                session.State.UnstagedFocusedIndex,
                "Up Arrow wrapped from the first worktree row to the last row.");

            (int X, int Y) firstStagedRow;
            (int X, int Y) lastStagedRow;
            using (var workspace = automator.CreateSnapshot())
            {
                firstStagedRow = FindText(workspace, "staged-00.txt");
                lastStagedRow = FindText(workspace, "staged-02.txt");
            }

            await new Hex1bTerminalInputSequenceBuilder()
                .Ctrl()
                .ClickAt(firstStagedRow.X + 1, firstStagedRow.Y, MouseButton.Left)
                .Build()
                .ApplyAsync(terminal, timeout.Token);
            await new Hex1bTerminalInputSequenceBuilder()
                .Shift()
                .ClickAt(lastStagedRow.X + 1, lastStagedRow.Y, MouseButton.Left)
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
            var unstageHunk = FindText(stagedSnapshot, "Unstage hunk");
            await automator.ClickAtAsync(unstageHunk.X + 1, unstageHunk.Y, MouseButton.Left, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.UnstageFocusedHunkCallCount == 2,
                TimeSpan.FromSeconds(3),
                "Focused-hunk unstaging is mouse-activatable");

            await automator.DoubleClickAtAsync(10, 4, MouseButton.Left, timeout.Token);
            await automator.DragAsync(20, 10, 20, 14, MouseButton.Left, timeout.Token);
            using var actionsBeforeClick = automator.CreateSnapshot();
            var actionY = FindText(actionsBeforeClick, "Less context").Y;
            var actionsBeforeClickLine = actionsBeforeClick.GetLine(actionY);
            var stageX = actionsBeforeClickLine.IndexOf("Stage", StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, stageX);
            await automator.ClickAtAsync(stageX + 1, actionY, MouseButton.Left, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.StageCallCount == 1,
                TimeSpan.FromSeconds(3),
                "Stage button is mouse-activatable");
            var unstageX = actionsBeforeClickLine.IndexOf("Unstage", StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, unstageX);
            await automator.ClickAtAsync(unstageX + 1, actionY, MouseButton.Left, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.UnstageCallCount == 1,
                TimeSpan.FromSeconds(3),
                "Unstage button is mouse-activatable");
            using var snapshot = automator.CreateSnapshot();
            actionY = FindText(snapshot, "Less context").Y;
            var actionLine = snapshot.GetLine(actionY);
            var stageAllX = actionLine.IndexOf("Stage all", StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, stageAllX);
            await automator.ClickAtAsync(stageAllX + 1, actionY, MouseButton.Left, timeout.Token);
            var unstageAllX = actionLine.IndexOf("Unstage all", StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, unstageAllX);
            await automator.ClickAtAsync(unstageAllX + 1, actionY, MouseButton.Left, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.StageAllCallCount == 2 && session.UnstageAllCallCount == 2,
                TimeSpan.FromSeconds(3),
                "Stage-all and unstage-all actions are mouse-activatable");
            var lessContextX = actionLine.IndexOf("Less context", StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, lessContextX);
            await automator.ClickAtAsync(lessContextX + 1, actionY, MouseButton.Left, timeout.Token);
            var moreContextX = actionLine.IndexOf("More context", StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, moreContextX);
            await automator.ClickAtAsync(moreContextX + 1, actionY, MouseButton.Left, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.DecreaseDiffContextCallCount == 2 && session.IncreaseDiffContextCallCount == 2,
                TimeSpan.FromSeconds(3),
                "Diff context actions are mouse-activatable");
            await automator.ClickAtAsync(70, 18, MouseButton.Left, timeout.Token);
            await automator.TypeAsync("mouse commit", timeout.Token);
            var commitX = actionLine.IndexOf("Commit", StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, commitX);
            await automator.ClickAtAsync(commitX + 1, actionY, MouseButton.Left, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.CommitCallCount == 2 && session.CommitMessage.Message.Length == 0,
                TimeSpan.FromSeconds(3),
                "Commit is mouse-activatable and clears a successful draft");
            var hunkActionX = actionLine.IndexOf("Stage hunk", StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, hunkActionX);
            await automator.ClickAtAsync(hunkActionX + 1, actionY, MouseButton.Left, timeout.Token);
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
            .WithDimensions(320, 30)
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
            await automator.WaitUntilTextAsync("Mode: executable", TimeSpan.FromSeconds(3));
            using (var ready = automator.CreateSnapshot())
            {
                var stage = FindTextOnLineWith(ready, "Stage", "Mode: executable");
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
            var done = application!.Focusables
                .OfType<ButtonNode>()
                .Single(static button => string.Equals(button.Label, "Done", StringComparison.Ordinal));
            var doneBounds = done.HitTestBounds;

            await automator.ClickAtAsync(
                doneBounds.X + (doneBounds.Width / 2),
                doneBounds.Y,
                MouseButton.Left,
                timeout.Token);
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
            using (var confirmation = automator.CreateSnapshot())
            {
                AssertWindowFrameIsComplete(confirmation, "Commit without hooks?", 58, 9);
            }

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
                _ => session.CommitWithoutHooksCallCount == 1 &&
                    session.Activity == "Commit completed without bypassable hooks",
                TimeSpan.FromSeconds(3),
                "Explicit pointer approval completes the separate hook-bypass transaction");
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
            await automator.WaitUntilTextAsync("Commit", TimeSpan.FromSeconds(3));
            await automator.KeyAsync(Hex1bKey.F4, timeout.Token);
            await automator.WaitUntilTextAsync("Amend published commit?", TimeSpan.FromSeconds(3));
            using (var warning = automator.CreateSnapshot())
            {
                AssertWindowFrameIsComplete(warning, "Amend published commit?", 78, 15);
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
                snapshot => session.CommitAfterWarningsCallCount == 1 &&
                    !snapshot.ContainsText("Amend published commit?") &&
                    snapshot.ContainsText("Options"),
                TimeSpan.FromSeconds(3),
                "Pointer approval completes and closes the published-amend confirmation");
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
                AssertWindowFrameIsComplete(warning, "Commit without hooks?", 78, 15);
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
            await automator.WaitUntilTextAsync("Commit", TimeSpan.FromSeconds(3));
            await automator.KeyAsync(Hex1bKey.F4, timeout.Token);
            await automator.WaitUntilTextAsync("Commit detached HEAD?", TimeSpan.FromSeconds(3));
            using (var warning = automator.CreateSnapshot())
            {
                AssertWindowFrameIsComplete(warning, "Commit detached HEAD?", 78, 13);
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
            await automator.WaitUntilTextAsync("Abort", TimeSpan.FromSeconds(3));
            using (var workspace = automator.CreateSnapshot())
            {
                var abortPosition = FindText(workspace, "Abort");
                await automator.ClickAtAsync(
                    abortPosition.X + 1,
                    abortPosition.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilTextAsync("Abort merge?", TimeSpan.FromSeconds(3));
            using (var confirmation = automator.CreateSnapshot())
            {
                AssertWindowFrameIsComplete(confirmation, "Abort merge?", 78, 16);
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
                var abortPosition = FindText(workspace, "Abort");
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
            .WithDimensions(320, 30)
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
    /// Verifies revert defaults to cancellation and every exact scope plus undo is keyboard-and-pointer operable.
    /// </summary>
    [TestMethod]
    public async Task Revert_WithKeyboardAndMouseInput_ConfirmsScopesAndExposesOneLevelUndo()
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
            .WithDimensions(320, 30)
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
            using (var workspace = automator.CreateSnapshot())
            {
                var changedPath = FindText(workspace, "worktree.txt");
                await automator.ClickAtAsync(
                    changedPath.X + 1,
                    changedPath.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.TypeAsync("r", timeout.Token);
            await automator.WaitUntilTextAsync(
                "Revert worktree changes?",
                TimeSpan.FromSeconds(3));
            await automator.KeyAsync(Hex1bKey.Escape, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Revert worktree changes?"),
                TimeSpan.FromSeconds(3),
                "Lowercase r opens revert confirmation from the changed-file list");
            await automator.ClickAtAsync(55, 6, MouseButton.Left, timeout.Token);
            await automator.TypeAsync("r", timeout.Token);
            await automator.WaitUntilTextAsync(
                "Revert worktree changes?",
                TimeSpan.FromSeconds(3));
            await automator.KeyAsync(Hex1bKey.Escape, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Revert worktree changes?"),
                TimeSpan.FromSeconds(3),
                "Lowercase r opens revert confirmation from the diff editor");
            await automator.TypeAsync("R", timeout.Token);
            await automator.WaitUntilTextAsync(
                "Revert worktree changes?",
                TimeSpan.FromSeconds(3));
            await automator.KeyAsync(Hex1bKey.Escape, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Revert worktree changes?"),
                TimeSpan.FromSeconds(3),
                "Uppercase R opens revert confirmation from the diff editor");
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
            await automator.WaitUntilTextAsync("Undo revert", TimeSpan.FromSeconds(3));
            await automator.KeyAsync(
                Hex1bKey.Z,
                Hex1bModifiers.Control,
                timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.UndoRevertCallCount == 1 && !session.CanUndoRevert,
                TimeSpan.FromSeconds(3),
                "Ctrl+Z consumes the retained revert from the reconciled workspace");

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
            .WithDimensions(320, 30)
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
            .WithDimensions(60, 18)
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
            await automator.WaitUntilTextAsync("Branches", TimeSpan.FromSeconds(3));
            using (var workspace = automator.CreateSnapshot())
            {
                var branches = FindText(workspace, "Branches");
                await automator.ClickAtAsync(branches.X + 1, branches.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Branches and linked worktrees", TimeSpan.FromSeconds(3));
            Assert.AreEqual(1, session.LoadBranchesCallCount);
            using (var branchWindow = automator.CreateSnapshot())
            {
                AssertWindowFrameIsComplete(branchWindow, "Branches and linked worktrees", 58, 16);
                var rename = FindText(branchWindow, "Rename...");
                await automator.ClickAtAsync(
                    rename.X + 1,
                    rename.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilTextAsync("Rename local branch", TimeSpan.FromSeconds(3));
            using (var renameDialog = automator.CreateSnapshot())
            {
                AssertWindowFrameIsComplete(renameDialog, "Rename local branch", 58, 10);
            }

            await automator.KeyAsync(Hex1bKey.Escape, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Rename local branch") &&
                    snapshot.ContainsText("Branches and linked worktrees"),
                TimeSpan.FromSeconds(3),
                "Escape closes branch rename and returns to the branch window");
            using (var branchWindow = automator.CreateSnapshot())
            {
                var filter = FindText(branchWindow, "Filter:");
                await automator.ClickAtAsync(filter.X + 9, filter.Y, MouseButton.Left, timeout.Token);
            }

            await automator.TypeAsync("[<35;181;4m", timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => session.Branches.Filter.Text.Length == 0 &&
                    session.Branches.VisibleItems.Length == 3 &&
                    !snapshot.ContainsText("[<35;181;4m"),
                TimeSpan.FromSeconds(3),
                "A fragmented Windows mouse report never becomes branch-filter text");
            await automator.TypeAsync("feature", timeout.Token);
            await automator.WaitUntilTextAsync("Delete...", TimeSpan.FromSeconds(3));
            using (var filtered = automator.CreateSnapshot())
            {
                var delete = FindText(filtered, "Delete...");
                await automator.ClickAtAsync(
                    delete.X + 1,
                    delete.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilTextAsync("Delete branch?", TimeSpan.FromSeconds(3));
            using (var deleteDialog = automator.CreateSnapshot())
            {
                AssertWindowFrameIsComplete(deleteDialog, "Delete branch?", 58, 12);
            }

            await automator.KeyAsync(Hex1bKey.Escape, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Delete branch?") &&
                    snapshot.ContainsText("Branches and linked worktrees"),
                TimeSpan.FromSeconds(3),
                "Escape closes branch deletion and returns to the branch window");
            using (var branchWindow = automator.CreateSnapshot())
            {
                var filter = FindText(branchWindow, "Filter:");
                await automator.ClickAtAsync(filter.X + 9, filter.Y, MouseButton.Left, timeout.Token);
            }

            await new Hex1bTerminalInputSequenceBuilder()
                .Ctrl()
                .Key(Hex1bKey.A)
                .Build()
                .ApplyAsync(terminal, timeout.Token);
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
                AssertWindowFrameIsComplete(createDialog, "Create local branch", 58, 11);
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
                _ => session.CreateBranchCallCount == 1 &&
                    string.Equals(session.LastBranchName, "team/topic-local", StringComparison.Ordinal) &&
                    ReferenceEquals(session.LastBranch, remoteBranch) &&
                    string.Equals(session.Activity, "Created tracked branch", StringComparison.Ordinal),
                TimeSpan.FromSeconds(3),
                "The mouse-activated branch transaction publishes its complete settled state");
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
    /// Verifies linked-worktree discovery and creation controls are fully mouse reachable.
    /// </summary>
    [TestMethod]
    public async Task WorktreeWindow_WithMouseInput_OpensFromBranchesAndCreatesLockedWorktree()
    {
        var main = CreateBranch("refs/heads/main", BranchKind.Local, isCurrent: true);
        var topic = CreateBranch("refs/heads/topic", BranchKind.Local, isCurrent: false);
        var session = new FakeRepositoryWorkspaceSession();
        session.ConfigureBranches(main, topic);
        session.ConfigureWorktrees(
            [main, topic],
            [
                CreateWorktree("main", main.FullName, isLocked: false, isPrunable: false),
                CreateWorktree("linked-topic", topic.FullName, isLocked: false, isPrunable: false),
            ]);
        var view = new RepositoryWorkspaceView(
            new GitSailShellOptions(ApplicationMode.Gui, WorkingDirectory: null),
            session,
            CancellationToken.None);
        Hex1bApp? application = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
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
        var automator = new Hex1bTerminalAutomator(terminal, TimeSpan.FromSeconds(6));

        try
        {
            await automator.KeyAsync(Hex1bKey.F8, timeout.Token);
            await automator.WaitUntilTextAsync("Branches and linked worktrees", TimeSpan.FromSeconds(6));
            using (var branches = automator.CreateSnapshot())
            {
                var worktrees = FindText(branches, "Worktrees...");
                await automator.ClickAtAsync(
                    worktrees.X + 1,
                    worktrees.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilTextAsync("Linked worktrees", TimeSpan.FromSeconds(6));
            Assert.AreEqual(1, session.LoadWorktreesCallCount);
            using (var worktrees = automator.CreateSnapshot())
            {
                AssertWindowFrameIsComplete(worktrees, "Linked worktrees", 78, 22);
                var linked = FindText(worktrees, "linked-topic");
                await automator.ClickAtAsync(linked.X + 2, linked.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Move...", TimeSpan.FromSeconds(6));
            using (var selected = automator.CreateSnapshot())
            {
                Assert.IsTrue(selected.ContainsText("Lock..."));
                Assert.IsTrue(selected.ContainsText("Remove..."));
            }

            using (var selected = automator.CreateSnapshot())
            {
                var move = FindText(selected, "Move...");
                await automator.ClickAtAsync(move.X + 1, move.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Move linked worktree", TimeSpan.FromSeconds(6));
            using (var dialog = automator.CreateSnapshot())
            {
                AssertWindowFrameIsComplete(dialog, "Move linked worktree", 78, 13);
            }

            await automator.KeyAsync(Hex1bKey.Escape, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Move linked worktree"),
                TimeSpan.FromSeconds(6),
                "Escape closes the move-worktree dialog");
            using (var selected = automator.CreateSnapshot())
            {
                var locking = FindText(selected, "Lock...");
                await automator.ClickAtAsync(locking.X + 1, locking.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Lock linked worktree", TimeSpan.FromSeconds(6));
            using (var dialog = automator.CreateSnapshot())
            {
                AssertWindowFrameIsComplete(dialog, "Lock linked worktree", 78, 12);
            }

            await automator.KeyAsync(Hex1bKey.Escape, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Lock linked worktree"),
                TimeSpan.FromSeconds(6),
                "Escape closes the lock-worktree dialog");
            using (var selected = automator.CreateSnapshot())
            {
                var remove = FindText(selected, "Remove...");
                await automator.ClickAtAsync(remove.X + 1, remove.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Remove linked worktree?", TimeSpan.FromSeconds(6));
            using (var dialog = automator.CreateSnapshot())
            {
                AssertWindowFrameIsComplete(dialog, "Remove linked worktree?", 78, 16);
            }

            await automator.KeyAsync(Hex1bKey.Escape, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Remove linked worktree?"),
                TimeSpan.FromSeconds(6),
                "Escape closes the remove-worktree confirmation");
            using (var selected = automator.CreateSnapshot())
            {
                var prune = FindText(selected, "Prune stale...");
                await automator.ClickAtAsync(prune.X + 1, prune.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync(
                "Prune stale linked-worktree records?",
                TimeSpan.FromSeconds(6));
            using (var dialog = automator.CreateSnapshot())
            {
                AssertWindowFrameIsComplete(
                    dialog,
                    "Prune stale linked-worktree records?",
                    78,
                    22);
            }

            await automator.KeyAsync(Hex1bKey.Escape, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Prune stale linked-worktree records?"),
                TimeSpan.FromSeconds(6),
                "Escape closes the worktree-prune preview");
            using (var selected = automator.CreateSnapshot())
            {
                var repair = FindText(selected, "Repair...");
                await automator.ClickAtAsync(repair.X + 1, repair.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Repair worktree connection", TimeSpan.FromSeconds(6));
            using (var dialog = automator.CreateSnapshot())
            {
                AssertWindowFrameIsComplete(dialog, "Repair worktree connection", 78, 13);
            }

            await automator.KeyAsync(Hex1bKey.Escape, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Repair worktree connection"),
                TimeSpan.FromSeconds(6),
                "Escape closes the worktree-repair dialog");
            using (var selected = automator.CreateSnapshot())
            {
                var create = FindText(selected, "Create...");
                await automator.ClickAtAsync(create.X + 1, create.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Create linked worktree", TimeSpan.FromSeconds(6));
            using (var dialog = automator.CreateSnapshot())
            {
                AssertWindowFrameIsComplete(dialog, "Create linked worktree", 78, 22);
                var target = FindText(dialog, "Target:");
                await automator.ClickAtAsync(target.X + 9, target.Y, MouseButton.Left, timeout.Token);
            }

            await new Hex1bTerminalInputSequenceBuilder()
                .Ctrl()
                .Key(Hex1bKey.A)
                .Key(Hex1bKey.Backspace)
                .Build()
                .ApplyAsync(terminal, timeout.Token);
            await automator.TypeAsync("new-worktree", timeout.Token);
            using (var dialog = automator.CreateSnapshot())
            {
                var branchName = FindText(dialog, "New branch:");
                await automator.ClickAtAsync(
                    branchName.X + "New branch: ".Length + 1,
                    branchName.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.TypeAsync("worktree-branch", timeout.Token);
            using (var dialog = automator.CreateSnapshot())
            {
                var locking = FindText(dialog, "[ ] Lock after creation");
                await automator.ClickAtAsync(
                    locking.X + 1,
                    locking.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilTextAsync("[x] Lock after creation", TimeSpan.FromSeconds(6));
            using (var ready = automator.CreateSnapshot())
            {
                var create = FindTextOnLineWith(ready, "Create", "Cancel");
                await automator.ClickAtAsync(create.X + 1, create.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilAsync(
                _ => session.AddWorktreeCallCount == 1 && session.LoadWorktreesCallCount == 2,
                TimeSpan.FromSeconds(6),
                "The mouse-submitted worktree creation refreshes the controlled catalog");
            Assert.AreEqual("worktree-branch", session.LastBranchName);
            Assert.AreSame(main, session.LastBranch);
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
                var reset = FindText(branches, "Reset...");
                await automator.ClickAtAsync(reset.X + 1, reset.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Reset current branch", TimeSpan.FromSeconds(3));
            using (var reset = automator.CreateSnapshot())
            {
                AssertWindowFrameIsComplete(reset, "Reset current branch", 78, 13);
                Assert.IsTrue(reset.ContainsText("Hard reset"));
            }

            await automator.KeyAsync(Hex1bKey.Escape, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Reset current branch") &&
                    snapshot.ContainsText("Branches and linked worktrees"),
                TimeSpan.FromSeconds(3),
                "Escape cancels reset and returns to the branch workspace");
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
            using (var dialog = automator.CreateSnapshot())
            {
                AssertWindowFrameIsComplete(dialog, "Merge exact selected branch?", 78, 19);
            }

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
                AssertWindowFrameIsComplete(remotes, "Remotes and transport", 78, 22);
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
            using (var dialog = automator.CreateSnapshot())
            {
                AssertWindowFrameIsComplete(dialog, "Fetch upstream?", 78, 12);
            }

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
                AssertWindowFrameIsComplete(dialog, "Add remote", 78, 10);
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
            using (var dialog = automator.CreateSnapshot())
            {
                AssertWindowFrameIsComplete(
                    dialog,
                    "Fetch every configured remote?",
                    78,
                    12);
            }

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
                AssertWindowFrameIsComplete(dialog, "Prune stale remote refs?", 78, 18);
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
                AssertWindowFrameIsComplete(dialog, "Remove configured remote?", 78, 14);
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
    /// Verifies duplicate-safe URL selection, redaction, cancel-first review, and mouse initialization at 80 by 24.
    /// </summary>
    [TestMethod]
    public async Task RemoteInitialization_AtEightyByTwentyFour_SelectsAndConfirmsExactTarget()
    {
        var secretUrl = RemoteUrl.FromText(
            "https://person:password@example.invalid/team/repository.git?token=secret");
        var targetPath = OperatingSystem.IsWindows()
            ? "C:\\repositories\\new-remote.git"
            : "/repositories/new-remote.git";
        var localUrl = RemoteUrl.FromText(targetPath);
        var remote = new RemoteInfo(
            RemoteName.FromBytes("origin"u8),
            [secretUrl],
            [secretUrl, localUrl]);
        var target = new RemoteInitializationTarget(
            localUrl,
            RemoteInitializationKind.Local,
            targetPath,
            sshDestination: null,
            sshPort: null,
            remotePath: null);
        var plan = new RemoteInitializationPlan(
            new RemoteCatalog([remote]),
            remote,
            configuredUrlIndex: 1,
            target,
            RepositoryObjectFormat.Sha1,
            sshExecutable: null,
            sshDecoder: null);
        var session = new FakeRepositoryWorkspaceSession();
        session.ConfigureRemotes(remote);
        session.ConfigureRemoteInitializationPlan(plan);
        var view = new RepositoryWorkspaceView(
            new GitSailShellOptions(ApplicationMode.Gui, WorkingDirectory: null),
            session,
            CancellationToken.None);
        Hex1bApp? application = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(18));
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
            await OpenAndSelectInitializationUrlAsync(automator, timeout.Token);
            await automator.WaitUntilTextAsync(
                "Initialize exact bare repository?",
                TimeSpan.FromSeconds(3));
            Assert.AreEqual(1, session.PrepareRemoteInitializationCallCount);
            Assert.AreEqual(1, session.LastRemoteInitializationUrlIndex);
            using (var dialog = automator.CreateSnapshot())
            {
                AssertWindowFrameIsComplete(
                    dialog,
                    "Initialize exact bare repository?",
                    78,
                    22);
                Assert.IsTrue(dialog.ContainsText("Object format: SHA-1"));
                Assert.IsTrue(dialog.ContainsText("Transport: isolated local Git operation"));
                Assert.IsTrue(dialog.ContainsText(targetPath));
                Assert.IsFalse(dialog.ContainsText("person"));
                Assert.IsFalse(dialog.ContainsText("password"));
                Assert.IsFalse(dialog.ContainsText("token=secret"));
            }

            await automator.KeyAsync(Hex1bKey.Enter, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Initialize exact bare repository?"),
                TimeSpan.FromSeconds(3),
                "Initialization review defaults to cancel");
            Assert.AreEqual(0, session.InitializeRemoteCallCount);

            await OpenAndSelectInitializationUrlAsync(automator, timeout.Token);
            await automator.WaitUntilTextAsync(
                "Initialize exact bare repository?",
                TimeSpan.FromSeconds(3));
            using (var dialog = automator.CreateSnapshot())
            {
                var initialize = FindTextOnLineWith(
                    dialog,
                    "Initialize exact bare repository",
                    "Cancel");
                await automator.ClickAtAsync(
                    initialize.X + 1,
                    initialize.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilAsync(
                _ => session.InitializeRemoteCallCount == 1,
                TimeSpan.FromSeconds(3),
                "The exact initialization transaction is mouse activatable");
            Assert.AreSame(plan, session.LastRemoteInitializationPlan);
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }

        async Task OpenAndSelectInitializationUrlAsync(
            Hex1bTerminalAutomator activeAutomator,
            CancellationToken cancellationToken)
        {
            using (var remotes = activeAutomator.CreateSnapshot())
            {
                var initialize = FindText(remotes, "Initialize...");
                await activeAutomator.ClickAtAsync(
                    initialize.X + 1,
                    initialize.Y,
                    MouseButton.Left,
                    cancellationToken);
            }

            await activeAutomator.WaitUntilTextAsync(
                "Select a remote initialization URL",
                TimeSpan.FromSeconds(3));
            using (var selector = activeAutomator.CreateSnapshot())
            {
                AssertWindowFrameIsComplete(
                    selector,
                    "Select a remote initialization URL",
                    78,
                    22);
                Assert.IsTrue(selector.ContainsText("https://example.invalid/team/repository.git?<redacted>"));
                Assert.IsFalse(selector.ContainsText("person"));
                Assert.IsFalse(selector.ContainsText("password"));
                Assert.IsFalse(selector.ContainsText("token=secret"));
                var filter = FindText(selector, "Filter URLs:");
                await activeAutomator.ClickAtAsync(
                    filter.X + 14,
                    filter.Y,
                    MouseButton.Left,
                    cancellationToken);
            }

            await activeAutomator.TypeAsync("new-remote", cancellationToken);
            await activeAutomator.WaitUntilAsync(
                snapshot => snapshot.ContainsText(targetPath) &&
                    !snapshot.ContainsText("https://example.invalid/team/repository.git?<redacted>"),
                TimeSpan.FromSeconds(3),
                "Filtering retains only the exact local initialization URL");
            using var filtered = activeAutomator.CreateSnapshot();
            var review = FindTextOnLineWith(filtered, "Review exact target", "Cancel");
            await activeAutomator.ClickAtAsync(
                review.X + 1,
                review.Y,
                MouseButton.Left,
                cancellationToken);
        }
    }

    /// <summary>
    /// Verifies an authenticated transport secret is masked, mouse-submitted, and resumes its exact operation.
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task CredentialPrompt_AtEightyByTwentyFour_MasksAndSubmitsSecret()
    {
        var targetPath = OperatingSystem.IsWindows()
            ? "C:\\repositories\\credential-target.git"
            : "/repositories/credential-target.git";
        var targetUrl = RemoteUrl.FromText(targetPath);
        var remote = new RemoteInfo(
            RemoteName.FromBytes("origin"u8),
            [targetUrl],
            [targetUrl]);
        var target = new RemoteInitializationTarget(
            targetUrl,
            RemoteInitializationKind.Local,
            targetPath,
            sshDestination: null,
            sshPort: null,
            remotePath: null);
        var plan = new RemoteInitializationPlan(
            new RemoteCatalog([remote]),
            remote,
            configuredUrlIndex: 0,
            target,
            RepositoryObjectFormat.Sha1,
            sshExecutable: null,
            sshDecoder: null);
        var session = new FakeRepositoryWorkspaceSession();
        session.ConfigureRemotes(remote);
        session.ConfigureRemoteInitializationPlan(plan);
        session.ConfigureRemoteInitializationPrompt(
            CredentialPromptKind.Secret,
            "Password for 'ssh://example.invalid':");
        var view = new RepositoryWorkspaceView(
            new GitSailShellOptions(ApplicationMode.Gui, WorkingDirectory: null),
            session,
            CancellationToken.None);
        Hex1bApp? application = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(18));
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
                var initialize = FindText(remotes, "Initialize...");
                await automator.ClickAtAsync(
                    initialize.X + 1,
                    initialize.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilTextAsync(
                "Credential secret required",
                TimeSpan.FromSeconds(3));
            await automator.TypeAsync("hunter2", timeout.Token);
            await automator.WaitUntilTextAsync("•••••••", TimeSpan.FromSeconds(3));
            using (var prompt = automator.CreateSnapshot())
            {
                AssertWindowFrameIsComplete(prompt, "Credential secret required", 78, 14);
                Assert.IsTrue(prompt.ContainsText("Password for 'ssh://example.invalid':"));
                Assert.IsFalse(prompt.ContainsText("hunter2"));
                var submit = FindText(prompt, "Submit response");
                await automator.ClickAtAsync(
                    submit.X + 1,
                    submit.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilTextAsync(
                "Initialize exact bare repository?",
                TimeSpan.FromSeconds(3));
            Assert.AreEqual("hunter2", session.LastCredentialPromptResponse);
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
            using (var warning = automator.CreateSnapshot())
            {
                AssertWindowFrameIsComplete(
                    warning,
                    "Force push without an expected-OID lease?",
                    78,
                    22);
            }

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
                AssertWindowFrameIsComplete(
                    warning,
                    "Force push without an expected-OID lease?",
                    78,
                    22);
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
    /// Verifies exact tag and advertised-branch selectors are searchable, cancel-first, and mouse activatable.
    /// </summary>
    [TestMethod]
    public async Task RemoteReferenceDialogs_WithMouseInput_PushTagAndDeleteBranchWithExactPlans()
    {
        var remote = CreateRemote("origin", "ssh://developer@example.invalid/team/repository.git");
        var firstTag = RefName.FromBytes("refs/tags/ordinary"u8);
        var releaseTag = RefName.FromBytes("refs/tags/release/v2"u8);
        var mainBranch = RefName.FromBytes("refs/heads/main"u8);
        var featureBranch = RefName.FromBytes("refs/heads/team/feature"u8);
        var tagPlan = CreateExplicitPushPlan(
            remote,
            releaseTag,
            releaseTag,
            PushRelationship.New);
        var deletionPlan = CreateExplicitPushPlan(
            remote,
            source: null,
            featureBranch,
            PushRelationship.Delete);
        var session = new FakeRepositoryWorkspaceSession();
        session.ConfigureRemotes(remote);
        session.ConfigureLocalTags(firstTag, releaseTag);
        session.ConfigureRemoteBranches(mainBranch, featureBranch);
        session.ConfigurePushPlan(tagPlan);
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
            await OpenAndSelectReferenceAsync(
                automator,
                "Push tag...",
                "Push an exact local tag",
                releaseTag.DisplayText,
                "Review exact tag push",
                timeout.Token);
            await automator.WaitUntilTextAsync("Push exact tag plan?", TimeSpan.FromSeconds(3));
            Assert.AreEqual(1, session.LoadLocalTagsCallCount);
            Assert.AreEqual(1, session.PrepareTagPushCallCount);
            Assert.AreEqual(releaseTag, session.LastTag);
            await automator.KeyAsync(Hex1bKey.Enter, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Push exact tag plan?"),
                TimeSpan.FromSeconds(3),
                "The exact tag plan defaults to cancel");
            Assert.AreEqual(0, session.PushCallCount);

            await OpenAndSelectReferenceAsync(
                automator,
                "Push tag...",
                "Push an exact local tag",
                releaseTag.DisplayText,
                "Review exact tag push",
                timeout.Token);
            await automator.WaitUntilTextAsync("Push exact tag plan?", TimeSpan.FromSeconds(3));
            using (var planDialog = automator.CreateSnapshot())
            {
                AssertWindowFrameIsComplete(planDialog, "Push exact tag plan?", 78, 22);
                Assert.IsTrue(planDialog.ContainsText(releaseTag.DisplayText));
                var push = FindTextOnLineWith(planDialog, "Push exact tag", "Cancel");
                await automator.ClickAtAsync(push.X + 1, push.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilAsync(
                _ => session.PushCallCount == 1,
                TimeSpan.FromSeconds(3),
                "The exact tag push is pointer activatable");
            Assert.AreEqual(PushSafetyMode.Normal, session.LastPushOptions?.SafetyMode);
            Assert.AreEqual(GitOptionOverride.Disabled, session.LastPushOptions?.FollowTags);

            session.ConfigurePushPlan(deletionPlan);
            await OpenRemoteWorkspaceWithMouseAsync(automator, session, 2, timeout.Token);
            await OpenAndSelectReferenceAsync(
                automator,
                "Delete branch...",
                "Delete an exact advertised remote branch",
                featureBranch.DisplayText,
                "Review exact deletion",
                timeout.Token);
            await automator.WaitUntilTextAsync("Delete exact remote branch?", TimeSpan.FromSeconds(3));
            Assert.AreEqual(1, session.LoadRemoteBranchesCallCount);
            Assert.AreEqual(1, session.PrepareRemoteBranchDeletionCallCount);
            Assert.AreEqual(featureBranch, session.LastRemoteBranch);
            using (var deletionDialog = automator.CreateSnapshot())
            {
                AssertWindowFrameIsComplete(
                    deletionDialog,
                    "Delete exact remote branch?",
                    78,
                    22);
                Assert.IsTrue(deletionDialog.ContainsText("Safety: allow rewrite with exact leases"));
                Assert.IsTrue(deletionDialog.ContainsText("Relationship: delete"));
                var delete = FindTextOnLineWith(
                    deletionDialog,
                    "Delete exact remote branch",
                    "Cancel");
                await automator.ClickAtAsync(delete.X + 1, delete.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilAsync(
                _ => session.PushCallCount == 2,
                TimeSpan.FromSeconds(3),
                "The exact remote branch deletion is pointer activatable");
            Assert.AreEqual(PushSafetyMode.ExplicitLease, session.LastPushOptions?.SafetyMode);
            Assert.AreEqual(GitOptionOverride.Disabled, session.LastPushOptions?.FollowTags);
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies top-level popups close on outside clicks and the stash window closes from every focus area.
    /// </summary>
    [TestMethod]
    public async Task PopupDismissal_WithMouseAndEscape_ClosesTheActivePopup()
    {
        var stash = CreateStash(0, '1', "On main: escape target");
        var session = new FakeRepositoryWorkspaceSession();
        session.ConfigureStashes(stash);
        session.ConfigureBranches(CreateBranch("refs/heads/main", BranchKind.Local, isCurrent: true));
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
            await automator.KeyAsync(Hex1bKey.F1, timeout.Token);
            await automator.WaitUntilTextAsync("Help and keyboard reference", TimeSpan.FromSeconds(3));
            await automator.ClickAtAsync(0, 0, MouseButton.Left, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Help and keyboard reference"),
                TimeSpan.FromSeconds(3),
                "Clicking outside help closes it");

            await automator.KeyAsync(Hex1bKey.F2, timeout.Token);
            await automator.WaitUntilTextAsync("Command palette", TimeSpan.FromSeconds(3));
            using (var palette = automator.CreateSnapshot())
            {
                var filter = FindText(palette, "Find action:");
                await automator.ClickAtAsync(filter.X + 14, filter.Y, MouseButton.Left, timeout.Token);
            }

            await automator.KeyAsync(Hex1bKey.Escape, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Command palette"),
                TimeSpan.FromSeconds(3),
                "Escape closes the command palette from its filter");

            await automator.KeyAsync(Hex1bKey.F2, timeout.Token);
            await automator.WaitUntilTextAsync("Command palette", TimeSpan.FromSeconds(3));
            await automator.ClickAtAsync(0, 0, MouseButton.Left, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Command palette"),
                TimeSpan.FromSeconds(3),
                "Clicking outside the command palette closes it");

            await automator.KeyAsync(Hex1bKey.F8, timeout.Token);
            await automator.WaitUntilTextAsync("Branches and linked worktrees", TimeSpan.FromSeconds(3));
            using (var branches = automator.CreateSnapshot())
            {
                var filter = FindText(branches, "Filter:");
                await automator.ClickAtAsync(filter.X + 10, filter.Y, MouseButton.Left, timeout.Token);
            }

            await automator.KeyAsync(Hex1bKey.Escape, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Branches and linked worktrees"),
                TimeSpan.FromSeconds(3),
                "Escape closes the branch window from its filter");

            await automator.KeyAsync(Hex1bKey.F8, timeout.Token);
            await automator.WaitUntilTextAsync("Branches and linked worktrees", TimeSpan.FromSeconds(3));
            await automator.ClickAtAsync(0, 0, MouseButton.Left, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Branches and linked worktrees"),
                TimeSpan.FromSeconds(3),
                "Clicking outside the branch window closes it");

            await automator.KeyAsync(Hex1bKey.F9, timeout.Token);
            await automator.WaitUntilTextAsync("Stashes and exact patches", TimeSpan.FromSeconds(3));
            using (var stashWindow = automator.CreateSnapshot())
            {
                var filter = FindText(stashWindow, "Filter:");
                await automator.ClickAtAsync(filter.X + 10, filter.Y, MouseButton.Left, timeout.Token);
            }

            await automator.KeyAsync(Hex1bKey.Escape, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Stashes and exact patches"),
                TimeSpan.FromSeconds(3),
                "Escape closes the stash window from its filter");

            await automator.KeyAsync(Hex1bKey.F9, timeout.Token);
            await automator.WaitUntilTextAsync("Stashes and exact patches", TimeSpan.FromSeconds(3));
            using (var stashWindow = automator.CreateSnapshot())
            {
                var row = FindText(stashWindow, "escape target");
                await automator.ClickAtAsync(row.X + 2, row.Y, MouseButton.Left, timeout.Token);
            }

            await automator.KeyAsync(Hex1bKey.Escape, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Stashes and exact patches"),
                TimeSpan.FromSeconds(3),
                "Escape closes the stash window from its list");

            await automator.KeyAsync(Hex1bKey.F9, timeout.Token);
            await automator.WaitUntilTextAsync("+On main: escape target", TimeSpan.FromSeconds(3));
            using (var stashWindow = automator.CreateSnapshot())
            {
                var preview = FindText(stashWindow, "+On main: escape target");
                await automator.ClickAtAsync(preview.X + 2, preview.Y, MouseButton.Left, timeout.Token);
            }

            await automator.KeyAsync(Hex1bKey.Escape, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Stashes and exact patches"),
                TimeSpan.FromSeconds(3),
                "Escape closes the stash window from its preview");

            await automator.KeyAsync(Hex1bKey.F9, timeout.Token);
            await automator.WaitUntilTextAsync("Stashes and exact patches", TimeSpan.FromSeconds(3));
            await automator.ClickAtAsync(0, 0, MouseButton.Left, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Stashes and exact patches"),
                TimeSpan.FromSeconds(3),
                "Clicking outside the stash window closes it");

            await automator.KeyAsync(Hex1bKey.F9, timeout.Token);
            await automator.WaitUntilTextAsync("Stashes and exact patches", TimeSpan.FromSeconds(3));
            using (var stashWindow = automator.CreateSnapshot())
            {
                var create = FindText(stashWindow, "New...");
                await automator.ClickAtAsync(create.X + 1, create.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Save current changes to a stash", TimeSpan.FromSeconds(3));
            await automator.KeyAsync(Hex1bKey.Escape, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Save current changes to a stash") &&
                    snapshot.ContainsText("Stashes and exact patches"),
                TimeSpan.FromSeconds(3),
                "Escape closes only the active nested stash dialog");

            using (var stashWindow = automator.CreateSnapshot())
            {
                var pop = FindText(stashWindow, "Pop...");
                await automator.ClickAtAsync(pop.X + 1, pop.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Pop stash?", TimeSpan.FromSeconds(3));
            await automator.KeyAsync(Hex1bKey.Escape, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Pop stash?") &&
                    snapshot.ContainsText("Stashes and exact patches"),
                TimeSpan.FromSeconds(3),
                "Escape closes only the active nested stash confirmation");
            await automator.KeyAsync(Hex1bKey.Escape, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Stashes and exact patches"),
                TimeSpan.FromSeconds(3),
                "Escape closes the parent stash window after a nested dialog");
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies searchable stash preview, typed create options, and cancel-first apply, pop, and drop are mouse reachable.
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
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
            await automator.WaitUntilTextAsync("Stashes", TimeSpan.FromSeconds(3));
            await automator.KeyAsync(Hex1bKey.F9, timeout.Token);

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
                AssertWindowFrameIsComplete(
                    createDialog,
                    "Save current changes to a stash",
                    78,
                    13);
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
            Assert.AreEqual("Saved current changes to a stash", session.Activity);

            using (var workspace = automator.CreateSnapshot())
            {
                var stashes = FindTextOnLineWith(workspace, "Stashes", "Git 2.50.0");
                await automator.ClickAtAsync(stashes.X + 1, stashes.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Stashes and exact patches", TimeSpan.FromSeconds(3));
            using (var stashWindow = automator.CreateSnapshot())
            {
                var apply = FindText(stashWindow, "Apply...");
                await automator.ClickAtAsync(apply.X + 1, apply.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Apply stash?", TimeSpan.FromSeconds(3));
            await automator.KeyAsync(Hex1bKey.Enter, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Apply stash?"),
                TimeSpan.FromSeconds(3),
                "The first focused apply action cancels without mutation");
            Assert.AreEqual(0, session.ApplyStashCallCount);
            using (var stashWindow = automator.CreateSnapshot())
            {
                var apply = FindText(stashWindow, "Apply...");
                await automator.ClickAtAsync(apply.X + 1, apply.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Apply stash?", TimeSpan.FromSeconds(3));
            using (var applyDialog = automator.CreateSnapshot())
            {
                AssertWindowFrameIsComplete(applyDialog, "Apply stash?", 78, 13);
                Assert.IsTrue(applyDialog.ContainsText(release.ObjectId.ToString()));
                var restore = FindText(applyDialog, "Restore index [ ]");
                await automator.ClickAtAsync(restore.X + 1, restore.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Restore index [x]", TimeSpan.FromSeconds(3));
            using (var applyDialog = automator.CreateSnapshot())
            {
                var apply = FindTextOnLineWith(applyDialog, "Apply stash", "Cancel");
                await automator.ClickAtAsync(apply.X + 1, apply.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilAsync(
                _ => session.ApplyStashCallCount == 1,
                TimeSpan.FromSeconds(3),
                "The confirmed apply dispatches the exact focused stash");
            Assert.AreSame(release, session.LastStash);
            Assert.IsTrue(session.LastStashRestoreIndex);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Stashes and exact patches"),
                TimeSpan.FromSeconds(3),
                "The completed apply action closes its parent stash window");
            Assert.AreEqual("Applied stash", session.Activity);

            await automator.KeyAsync(Hex1bKey.F9, timeout.Token);

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
                AssertWindowFrameIsComplete(popDialog, "Pop stash?", 78, 13);
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
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Stashes and exact patches"),
                TimeSpan.FromSeconds(3),
                "The completed pop action closes its parent stash window");
            Assert.AreEqual("Popped stash", session.Activity);

            await automator.KeyAsync(Hex1bKey.F9, timeout.Token);

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
                AssertWindowFrameIsComplete(dropDialog, "Drop stash?", 78, 12);
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
                AssertWindowFrameIsComplete(compact, "Stashes and exact patches", 78, 22);
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
                AssertWindowFrameIsComplete(
                    createDialog,
                    "Save current changes to a stash",
                    78,
                    13);
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
                AssertWindowFrameIsComplete(help, "Help and keyboard reference", 58, 16);
                Assert.IsTrue(help.ContainsText("F2 searchable commands"));
                var doctor = FindTextOnLineWith(help, "Doctor", "Close");
                await automator.ClickAtAsync(doctor.X + 1, doctor.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Doctor and runtime capabilities", TimeSpan.FromSeconds(3));
            using (var doctor = automator.CreateSnapshot())
            {
                AssertWindowFrameIsComplete(doctor, "Doctor and runtime capabilities", 58, 14);
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
                AssertWindowFrameIsComplete(palette, "Command palette", 58, 16);
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
            await automator.WaitUntilAsync(
                snapshot => snapshot.ContainsText("Stashes and exact patches") &&
                    snapshot.ContainsText("palette target") &&
                    !snapshot.ContainsText("Command palette"),
                TimeSpan.FromSeconds(3),
                "The command palette closes after the stash workspace and its loaded row render");

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
    /// Verifies the command palette survives a resize to the supported minimum and remains fully operable.
    /// </summary>
    [TestMethod]
    public async Task CommandPalette_AtSixtyByEighteen_FitsCompleteFrameAndRunsSelection()
    {
        var session = new FakeRepositoryWorkspaceSession();
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
            await automator.KeyAsync(Hex1bKey.F2, timeout.Token);
            await automator.WaitUntilTextAsync("Command palette", TimeSpan.FromSeconds(3));
            terminal.Resize(60, 18);
            await automator.WaitUntilAsync(
                snapshot => snapshot.Width == 60 &&
                    snapshot.Height == 18 &&
                    snapshot.ContainsText("Command palette") &&
                    snapshot.ContainsText("Esc closes"),
                TimeSpan.FromSeconds(3),
                "The open command palette survives the resize to the minimum supported terminal");
            using (var palette = automator.CreateSnapshot())
            {
                AssertWindowFrameIsComplete(palette, "Command palette", 58, 16);
                Assert.IsTrue(palette.ContainsText("Find action:"));
                Assert.IsTrue(palette.ContainsText("Cancel"));
                Assert.IsTrue(palette.ContainsText("Esc closes"));
                var filter = FindText(palette, "Find action:");
                await automator.ClickAtAsync(filter.X + 14, filter.Y, MouseButton.Left, timeout.Token);
            }

            await automator.TypeAsync("refresh", timeout.Token);
            await automator.WaitUntilTextAsync("Repository: Refresh", TimeSpan.FromSeconds(3));
            await automator.KeyAsync(Hex1bKey.Enter, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => session.RefreshCallCount == 1 &&
                    !snapshot.ContainsText("Command palette"),
                TimeSpan.FromSeconds(3),
                "The minimum-size command palette runs the selected command and closes");

            await automator.KeyAsync(Hex1bKey.F2, timeout.Token);
            await automator.WaitUntilTextAsync("Command palette", TimeSpan.FromSeconds(3));
            await automator.KeyAsync(Hex1bKey.Escape, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Command palette"),
                TimeSpan.FromSeconds(3),
                "Escape closes the minimum-size command palette after a resize");
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies the active trace drawer opens from commands and mouse and dismisses by Escape and click-away.
    /// </summary>
    [TestMethod]
    public async Task TraceDrawer_WithActiveTrace_SupportsKeyboardMouseAndDismissal()
    {
        var tracePath = Path.Combine(Path.GetTempPath(), $"gitsail-ui-trace-{Guid.NewGuid():N}.jsonl");
        using var trace = TraceSession.Create(
            new TraceOptions(tracePath),
            new RuntimeProcessEnvironment(),
            TimeProvider.System);
        using var traceScope = ApplicationTrace.Begin(trace);
        trace.WriteApplicationStarted(ApplicationMode.Gui);
        var session = new FakeRepositoryWorkspaceSession();
        var view = new RepositoryWorkspaceView(
            new GitSailShellOptions(
                ApplicationMode.Gui,
                WorkingDirectory: null,
                Trace: new TraceOptions(tracePath)),
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
            await automator.KeyAsync(Hex1bKey.F2, timeout.Token);
            await automator.WaitUntilTextAsync("Command palette", TimeSpan.FromSeconds(3));
            using (var palette = automator.CreateSnapshot())
            {
                var filter = FindText(palette, "Find action:");
                await automator.ClickAtAsync(filter.X + 14, filter.Y, MouseButton.Left, timeout.Token);
            }

            await automator.TypeAsync("trace log", timeout.Token);
            await automator.WaitUntilTextAsync("View: Trace log", TimeSpan.FromSeconds(3));
            await automator.KeyAsync(Hex1bKey.Enter, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => snapshot.ContainsText("Trace log") &&
                    snapshot.ContainsText("application.started") &&
                    !snapshot.ContainsText("Command palette"),
                TimeSpan.FromSeconds(3),
                "The command palette closes after opening the active trace log");
            using (var drawer = automator.CreateSnapshot())
            {
                Assert.IsTrue(drawer.ContainsText("application.started"));
                Assert.IsTrue(drawer.ContainsText("Events omit command arguments"));
                Assert.IsTrue(drawer.ContainsText(Path.GetFileName(tracePath)));
            }

            await automator.KeyAsync(Hex1bKey.Escape, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Trace log"),
                TimeSpan.FromSeconds(3),
                "Escape closes the trace drawer");
            using (var workspace = automator.CreateSnapshot())
            {
                var traceButton = FindText(workspace, "Trace");
                await automator.ClickAtAsync(
                    traceButton.X + 1,
                    traceButton.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilTextAsync("Trace log", TimeSpan.FromSeconds(3));
            await automator.ClickAtAsync(0, 0, MouseButton.Left, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Trace log"),
                TimeSpan.FromSeconds(3),
                "Clicking outside closes the trace drawer");
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
            traceScope.Dispose();
            trace.Dispose();
            File.Delete(tracePath);
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

    private static void AssertWindowFrameIsComplete(
        Hex1bTerminalSnapshot snapshot,
        string title,
        int expectedWidth,
        int expectedHeight)
    {
        var titlePosition = FindText(snapshot, title);
        var left = titlePosition.X - 1;
        var top = titlePosition.Y - 1;
        var right = left + expectedWidth - 1;
        var bottom = top + expectedHeight - 1;

        Assert.IsGreaterThanOrEqualTo(0, left);
        Assert.IsGreaterThanOrEqualTo(0, top);
        Assert.IsLessThan(snapshot.Width, right);
        Assert.IsLessThan(snapshot.Height, bottom);
        Assert.AreEqual("┌", snapshot.GetCell(left, top).Character);
        Assert.AreEqual("┐", snapshot.GetCell(right, top).Character);
        Assert.AreEqual("└", snapshot.GetCell(left, bottom).Character);
        Assert.AreEqual("┘", snapshot.GetCell(right, bottom).Character);
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

    private static WorktreeInfo CreateWorktree(
        string pathTail,
        RefName? branchName,
        bool isLocked,
        bool isPrunable)
    {
        Assert.IsTrue(ObjectId.TryParseHex(
            "1111111111111111111111111111111111111111"u8,
            out var objectId));
        var path = OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath($"C:\\repository\\{pathTail}")
            : GitPath.FromUnixBytes(System.Text.Encoding.UTF8.GetBytes($"/repository/{pathTail}"));
        return new WorktreeInfo(
            path,
            objectId,
            branchName,
            isBare: false,
            isLocked,
            lockReasonDisplay: isLocked ? "portable volume" : null,
            isPrunable,
            prunableReasonDisplay: isPrunable ? "missing directory" : null);
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

    private static PushPlan CreateExplicitPushPlan(
        RemoteInfo remote,
        RefName? source,
        RefName destination,
        PushRelationship relationship)
    {
        Assert.IsTrue(ObjectId.TryParseHex(
            "2222222222222222222222222222222222222222"u8,
            out var sourceObjectId));
        Assert.IsTrue(ObjectId.TryParseHex(
            "1111111111111111111111111111111111111111"u8,
            out var expectedObjectId));
        var catalog = new RemoteCatalog([remote]);
        var expectation = new PushDestinationExpectation(
            remote.PushUrls.Single(),
            relationship == PushRelationship.New ? null : expectedObjectId,
            relationship,
            relationship == PushRelationship.Delete ? 0 : 3);
        var update = new PushUpdatePlan(
            new PushRefSpec(source, destination),
            source is null ? null : sourceObjectId,
            [expectation]);
        return new PushPlan(
            catalog,
            remote,
            [update],
            upstreamName: null,
            wouldSetUpstream: false,
            GitOptionOverride.Disabled);
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
        await automator.WaitUntilAsync(
            snapshot => snapshot.ContainsText("Remotes and transport") &&
                snapshot.ContainsText("Initialize...") &&
                snapshot.ContainsText("Push..."),
            TimeSpan.FromSeconds(3),
            "The loaded remote workspace renders its pointer actions before capture");
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
        using var dialog = automator.CreateSnapshot();
        AssertWindowFrameIsComplete(dialog, "Push exact Git default plan?", 78, 22);
    }

    private static async Task OpenAndSelectReferenceAsync(
        Hex1bTerminalAutomator automator,
        string actionLabel,
        string selectorTitle,
        string referenceName,
        string submitLabel,
        CancellationToken cancellationToken)
    {
        using (var remotes = automator.CreateSnapshot())
        {
            var action = FindText(remotes, actionLabel);
            await automator.ClickAtAsync(
                action.X + 1,
                action.Y,
                MouseButton.Left,
                cancellationToken);
        }

        await automator.WaitUntilTextAsync(selectorTitle, TimeSpan.FromSeconds(3));
        using (var selector = automator.CreateSnapshot())
        {
            AssertWindowFrameIsComplete(selector, selectorTitle, 78, 22);
            var reference = FindText(selector, referenceName);
            await automator.ClickAtAsync(
                reference.X + 1,
                reference.Y,
                MouseButton.Left,
                cancellationToken);
        }

        await automator.WaitUntilTextAsync($"Exact ref: {referenceName}", TimeSpan.FromSeconds(3));
        using (var selector = automator.CreateSnapshot())
        {
            var submit = FindTextOnLineWith(selector, submitLabel, "Cancel");
            await automator.ClickAtAsync(
                submit.X + 1,
                submit.Y,
                MouseButton.Left,
                cancellationToken);
        }
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
        await automator.WaitUntilAsync(
            snapshot => Enumerable.Range(0, snapshot.Height)
                .Select(snapshot.GetLine)
                .Any(static line => line.Contains("Revert...", StringComparison.Ordinal) &&
                    line.Contains("Undo revert", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(3),
            "The live action row exposes Undo revert");
        using var snapshot = automator.CreateSnapshot();
        var undoPosition = FindTextOnLineWith(snapshot, "Undo revert", "Revert...");
        await automator.ClickAtAsync(
            undoPosition.X + 1,
            undoPosition.Y,
            MouseButton.Left,
            cancellationToken);
    }
}
