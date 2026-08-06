using System.Text;
using GitSail.CommandLine;
using GitSail.Domain;
using GitSail.Ui;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;

namespace GitSail.UiTests;

/// <summary>
/// Verifies configured workspace bindings through the complete terminal input path.
/// </summary>
[TestClass]
public sealed class RepositoryWorkspaceKeymapTests
{
    /// <summary>
    /// Verifies a configured function key invokes its action and appears in the live palette.
    /// </summary>
    [TestMethod]
    public async Task Workspace_WithConfiguredRefreshBinding_UsesAndDisplaysOverride()
    {
        var session = new FakeRepositoryWorkspaceSession();
        session.ConfigureConfiguration(Configuration(
            "gitsail.keymap.repository.refresh",
            "F12"));
        var view = new RepositoryWorkspaceView(
            new GitSailShellOptions(ApplicationMode.Gui, WorkingDirectory: null),
            session,
            TestContext.Current!.CancellationToken);
        Hex1bApp? application = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
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
        var automator = new Hex1bTerminalAutomator(terminal, TimeSpan.FromSeconds(5));

        try
        {
            await automator.WaitUntilTextAsync("Commit message", TimeSpan.FromSeconds(5));
            await automator.KeyAsync(Hex1bKey.F12, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.RefreshCallCount == 1,
                TimeSpan.FromSeconds(5),
                "The configured F12 chord refreshes the workspace");
            await automator.KeyAsync(Hex1bKey.F2, timeout.Token);
            await automator.WaitUntilTextAsync("Command palette", TimeSpan.FromSeconds(5));
            using (var palette = automator.CreateSnapshot())
            {
                var filter = FindText(palette, "Find action:");
                await automator.ClickAtAsync(
                    filter.X + 14,
                    filter.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.TypeAsync("repository.refresh", timeout.Token);
            await automator.WaitUntilTextAsync("Refresh [F12]", TimeSpan.FromSeconds(5));
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies a colliding override leaves all defaults active and explains the exact failure.
    /// </summary>
    [TestMethod]
    public async Task Workspace_WithCollidingRefreshBinding_KeepsBaselineAndReportsFailure()
    {
        var session = new FakeRepositoryWorkspaceSession();
        session.ConfigureConfiguration(Configuration(
            "gitsail.keymap.repository.refresh",
            "F1"));
        var view = new RepositoryWorkspaceView(
            new GitSailShellOptions(ApplicationMode.Gui, WorkingDirectory: null),
            session,
            TestContext.Current!.CancellationToken);
        Hex1bApp? application = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
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
        var automator = new Hex1bTerminalAutomator(terminal, TimeSpan.FromSeconds(5));

        try
        {
            await automator.WaitUntilTextAsync("Commit message", TimeSpan.FromSeconds(5));
            await automator.KeyAsync(Hex1bKey.F1, timeout.Token);
            await automator.WaitUntilTextAsync(
                "Help and keyboard reference",
                TimeSpan.FromSeconds(5));
            using (var help = automator.CreateSnapshot())
            {
                var doctor = FindText(help, "Doctor");
                await automator.ClickAtAsync(
                    doctor.X + 1,
                    doctor.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilTextAsync(
                "configured overrides ignored",
                TimeSpan.FromSeconds(5));
            Assert.AreEqual(0, session.RefreshCallCount);
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies diff overrides replace printable key and character bindings without parent fallthrough.
    /// </summary>
    [TestMethod]
    public async Task Diff_WithConfiguredPrintableActions_UsesOnlyConfiguredChords()
    {
        var session = new FakeRepositoryWorkspaceSession(
            FakeRepositoryWorkspaceSession.CreateUnstagedEntry("worktree.txt"));
        session.ConfigureConfiguration(
            Configuration("gitsail.keymap.diff.stage-hunk", "F12"),
            Configuration("gitsail.keymap.diff.less-context", "F11"));
        var view = new RepositoryWorkspaceView(
            new GitSailShellOptions(ApplicationMode.Gui, WorkingDirectory: null),
            session,
            TestContext.Current!.CancellationToken);
        Hex1bApp? application = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
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
        var automator = new Hex1bTerminalAutomator(terminal, TimeSpan.FromSeconds(5));

        try
        {
            await automator.WaitUntilTextAsync("Unstaged: worktree.txt", TimeSpan.FromSeconds(5));
            await automator.ClickAtAsync(55, 6, MouseButton.Left, timeout.Token);
            await automator.KeyAsync(Hex1bKey.F12, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.StageFocusedHunkCallCount == 1,
                TimeSpan.FromSeconds(5),
                "F12 invokes the configured focused-hunk action");
            await automator.KeyAsync(Hex1bKey.F11, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.DecreaseDiffContextCallCount == 1,
                TimeSpan.FromSeconds(5),
                "F11 invokes the configured context action");
            await terminal.SendInputAsync("s["u8.ToArray(), timeout.Token);
            await automator.KeyAsync(Hex1bKey.F12, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.StageFocusedHunkCallCount == 2,
                TimeSpan.FromSeconds(5),
                "A later configured action proves the removed printable input was processed");
            Assert.AreEqual(0, session.StageCallCount);
            Assert.AreEqual(1, session.DecreaseDiffContextCallCount);
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies raw match shortcuts move within the active changed-path list and clamp at its bounds.
    /// </summary>
    [TestMethod]
    public async Task ChangedPathList_WithRawMatchShortcuts_MovesAndClampsFocusedPath()
    {
        var session = new FakeRepositoryWorkspaceSession(
            FakeRepositoryWorkspaceSession.CreateUnstagedEntry("file-00.txt"),
            FakeRepositoryWorkspaceSession.CreateUnstagedEntry("file-01.txt"),
            FakeRepositoryWorkspaceSession.CreateUnstagedEntry("file-02.txt"));
        var view = new RepositoryWorkspaceView(
            new GitSailShellOptions(ApplicationMode.Gui, WorkingDirectory: null),
            session,
            TestContext.Current!.CancellationToken);
        Hex1bApp? application = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
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
        var automator = new Hex1bTerminalAutomator(terminal, TimeSpan.FromSeconds(5));

        try
        {
            await automator.WaitUntilTextAsync("file-02.txt", TimeSpan.FromSeconds(5));
            await automator.ClickAtAsync(8, 4, MouseButton.Left, timeout.Token);

            await automator.WaitUntilAsync(
                _ => application!.FocusedNode is ListNode<StatusWorkspaceItem>,
                TimeSpan.FromSeconds(5),
                "The changed-path list owns keyboard focus");
            await terminal.SendInputAsync(" "u8.ToArray(), timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.StageCallCount == 1,
                TimeSpan.FromSeconds(5),
                "Raw Space stages the focused changed path");
            await terminal.SendInputAsync("nnnn"u8.ToArray(), timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.State.UnstagedFocusedIndex == 2,
                TimeSpan.FromSeconds(5),
                "Raw n reaches and clamps at the final matching changed path");
            await terminal.SendInputAsync("NNNN"u8.ToArray(), timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.State.UnstagedFocusedIndex == 0,
                TimeSpan.FromSeconds(5),
                "Raw Shift+N reaches and clamps at the first matching changed path");
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    private static GitConfigurationEntry Configuration(string key, string value)
        => new(
            GitConfigurationScope.Global,
            GitConfigurationOrigin.FromBytes("file:test"u8.ToArray()),
            GitConfigurationKey.FromBytes(Encoding.UTF8.GetBytes(key)),
            GitConfigurationValue.FromBytes(Encoding.UTF8.GetBytes(value)));

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

        Assert.Fail($"Text '{text}' was not present in the terminal snapshot.");
        return default;
    }
}
