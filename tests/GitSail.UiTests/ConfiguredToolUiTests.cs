using GitSail.CommandLine;
using GitSail.Domain;
using GitSail.Ui;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;

namespace GitSail.UiTests;

/// <summary>
/// Verifies configured tools remain reviewable, dismissible, keyboard-operable, and mouse-operable.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ConfiguredToolUiTests
{
    /// <summary>
    /// Verifies configured tools can be added, edited, and removed with exact scoped review.
    /// </summary>
    [TestMethod]
    public async Task ConfiguredToolManagement_WithMouseAndEscape_ReconcilesExactScope()
    {
        var session = new FakeRepositoryWorkspaceSession();
        var view = new RepositoryWorkspaceView(
            new GitSailShellOptions(ApplicationMode.Gui, WorkingDirectory: null),
            session,
            CancellationToken.None);
        Hex1bApp? application = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(120, 36)
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

            await automator.TypeAsync("manage configured tools", timeout.Token);
            await automator.WaitUntilTextAsync("Tools: Manage configured tools...", TimeSpan.FromSeconds(3));
            await automator.KeyAsync(Hex1bKey.Enter, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => session.ReloadConfigurationCallCount == 1 &&
                    snapshot.ContainsText("Manage configured tools") &&
                    snapshot.ContainsText("No configured tools"),
                TimeSpan.FromSeconds(3),
                "The palette opens scoped configured-tool management");

            using (var manager = automator.CreateSnapshot())
            {
                var add = FindText(manager, "Add...");
                await automator.ClickAtAsync(add.X + 1, add.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Add configured tool", TimeSpan.FromSeconds(3));
            await automator.KeyAsync(Hex1bKey.Escape, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Add configured tool") &&
                    snapshot.ContainsText("Manage configured tools"),
                TimeSpan.FromSeconds(3),
                "One Escape cancels editing without closing tool management");
            using (var manager = automator.CreateSnapshot())
            {
                var add = FindText(manager, "Add...");
                await automator.ClickAtAsync(add.X + 1, add.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Add configured tool", TimeSpan.FromSeconds(3));
            using (var editor = automator.CreateSnapshot())
            {
                var name = FindText(editor, "Name (used in guitool.<name>.*):");
                await automator.ClickAtAsync(name.X + 2, name.Y + 1, MouseButton.Left, timeout.Token);
            }

            await automator.TypeAsync("review", timeout.Token);
            using (var editor = automator.CreateSnapshot())
            {
                var command = FindText(editor, "Command (passed unchanged to the fixed platform shell):");
                await automator.ClickAtAsync(command.X + 2, command.Y + 1, MouseButton.Left, timeout.Token);
            }

            await automator.TypeAsync("printf review", timeout.Token);
            using (var editor = automator.CreateSnapshot())
            {
                var title = FindText(editor, "Title (empty uses the name):");
                await automator.ClickAtAsync(title.X + 2, title.Y + 1, MouseButton.Left, timeout.Token);
            }

            await automator.TypeAsync("Review tool", timeout.Token);
            await automator.WaitUntilTextAsync("Review add...", TimeSpan.FromSeconds(3));
            using (var editor = automator.CreateSnapshot())
            {
                var needsFile = FindText(editor, "Needs file: no");
                await automator.ClickAtAsync(needsFile.X + 6, needsFile.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Needs file: yes", TimeSpan.FromSeconds(3));
            using (var editor = automator.CreateSnapshot())
            {
                var review = FindText(editor, "Review add...");
                await automator.ClickAtAsync(review.X + 2, review.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilAsync(
                snapshot => snapshot.ContainsText("Add configured tool?") &&
                    snapshot.ContainsText("Scope: Repository (--local)") &&
                    snapshot.ContainsText("Command: printf review") &&
                    snapshot.ContainsText("Needs file: yes"),
                TimeSpan.FromSeconds(3),
                "Add review presents every exact scoped value");
            await automator.KeyAsync(Hex1bKey.Escape, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Add configured tool?") &&
                    snapshot.ContainsText("Review add..."),
                TimeSpan.FromSeconds(3),
                "One Escape closes only the add review");
            Assert.AreEqual(0, session.SaveConfiguredToolCallCount);

            using (var editor = automator.CreateSnapshot())
            {
                var review = FindText(editor, "Review add...");
                await automator.ClickAtAsync(review.X + 2, review.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Add exact tool", TimeSpan.FromSeconds(3));
            using (var confirmation = automator.CreateSnapshot())
            {
                var save = FindText(confirmation, "Add exact tool");
                await automator.ClickAtAsync(save.X + 2, save.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilAsync(
                snapshot => session.SaveConfiguredToolCallCount == 1 &&
                    session.LastConfigurationScope == GitConfigurationScope.Local &&
                    session.LastConfiguredToolConfiguration is
                    {
                        Name: "review",
                        Command: "printf review",
                        Title: "Review tool",
                        NeedsFile: true,
                    } && !snapshot.ContainsText("Add configured tool") &&
                    snapshot.ContainsText("Review tool  [review]") &&
                    snapshot.ContainsText("Edit...") &&
                    snapshot.ContainsText("Remove..."),
                TimeSpan.FromSeconds(3),
                "Confirmed add returns to the updated live tool catalog");

            using (var manager = automator.CreateSnapshot())
            {
                var edit = FindText(manager, "Edit...");
                await automator.ClickAtAsync(edit.X + 1, edit.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Edit configured tool", TimeSpan.FromSeconds(3));
            using (var editor = automator.CreateSnapshot())
            {
                var command = FindText(editor, "Command (passed unchanged to the fixed platform shell):");
                await automator.ClickAtAsync(command.X + 2, command.Y + 1, MouseButton.Left, timeout.Token);
            }

            await automator.KeyAsync(Hex1bKey.A, Hex1bModifiers.Control, timeout.Token);
            await automator.TypeAsync("printf updated", timeout.Token);
            await automator.WaitUntilTextAsync("printf updated", TimeSpan.FromSeconds(3));
            using (var editor = automator.CreateSnapshot())
            {
                var review = FindText(editor, "Review save...");
                await automator.ClickAtAsync(review.X + 2, review.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Save exact tool", TimeSpan.FromSeconds(3));
            await automator.WaitUntilTextAsync("Command: printf updated", TimeSpan.FromSeconds(3));
            using (var confirmation = automator.CreateSnapshot())
            {
                var save = FindText(confirmation, "Save exact tool");
                await automator.ClickAtAsync(save.X + 2, save.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilAsync(
                snapshot => session.SaveConfiguredToolCallCount == 2 &&
                    session.LastConfiguredToolConfiguration?.Command == "printf updated" &&
                    !snapshot.ContainsText("Edit configured tool") &&
                    snapshot.ContainsText("Manage configured tools") &&
                    snapshot.ContainsText("Command: printf updated") &&
                    snapshot.ContainsText("Remove..."),
                TimeSpan.FromSeconds(3),
                "Confirmed edit replaces the exact values and returns to management");

            using (var manager = automator.CreateSnapshot())
            {
                var remove = FindText(manager, "Remove...");
                await automator.ClickAtAsync(remove.X + 1, remove.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Remove configured tool?", TimeSpan.FromSeconds(3));
            await automator.KeyAsync(Hex1bKey.Escape, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Remove configured tool?") &&
                    snapshot.ContainsText("Remove..."),
                TimeSpan.FromSeconds(3),
                "One Escape closes only the remove review");
            Assert.AreEqual(0, session.RemoveConfiguredToolCallCount);
            using (var manager = automator.CreateSnapshot())
            {
                var remove = FindText(manager, "Remove...");
                await automator.ClickAtAsync(remove.X + 1, remove.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Remove exact tool", TimeSpan.FromSeconds(3));
            using (var confirmation = automator.CreateSnapshot())
            {
                var remove = FindText(confirmation, "Remove exact tool");
                await automator.ClickAtAsync(remove.X + 2, remove.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilAsync(
                snapshot => session.RemoveConfiguredToolCallCount == 1 &&
                    snapshot.ContainsText("No configured tools") &&
                    !snapshot.ContainsText("Review tool  [review]"),
                TimeSpan.FromSeconds(3),
                "Confirmed remove deletes only the selected scoped tool properties");
            await automator.KeyAsync(Hex1bKey.Escape, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Manage configured tools"),
                TimeSpan.FromSeconds(3),
                "One Escape closes configured-tool management");
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies the palette, preflight, capability review, denial, approval, and bounded output flow.
    /// </summary>
    [TestMethod]
    public async Task ConfiguredTool_ThroughCommandPalette_RequiresExplicitReviewAndShowsOutput()
    {
        var session = new FakeRepositoryWorkspaceSession(
            FakeRepositoryWorkspaceSession.CreateUnstagedEntry("review.txt"))
        {
            RequireConfiguredToolCapabilityReview = true,
        };
        session.ConfigureConfiguration(
            CreateConfigurationEntry("guitool.review.cmd", "printf 'review output'"),
            CreateConfigurationEntry("guitool.review.title", "Review changes"),
            CreateConfigurationEntry("guitool.review.confirm", "true"));
        var view = new RepositoryWorkspaceView(
            new GitSailShellOptions(ApplicationMode.Gui, WorkingDirectory: null),
            session,
            CancellationToken.None);
        Hex1bApp? application = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(120, 36)
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
            await OpenConfiguredToolPreflightAsync(automator, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => snapshot.ContainsText("Configured tool: Review changes") &&
                    snapshot.ContainsText("Exact configured command") &&
                    snapshot.ContainsText("printf 'review output'") &&
                    snapshot.ContainsText("Focused path: review.txt"),
                TimeSpan.FromSeconds(3),
                "Configured-tool preflight presents exact command and selected path");
            await automator.KeyAsync(Hex1bKey.Escape, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Configured tool: Review changes"),
                TimeSpan.FromSeconds(3),
                "One Escape cancels configured-tool preflight");
            Assert.AreEqual(0, session.RunConfiguredToolCallCount);

            await OpenConfiguredToolPreflightAsync(automator, timeout.Token);
            using (var preflight = automator.CreateSnapshot())
            {
                var continueButton = FindText(preflight, "Continue to security review");
                await automator.ClickAtAsync(
                    continueButton.X + 2,
                    continueButton.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilAsync(
                snapshot => snapshot.ContainsText("Configured command security review") &&
                    snapshot.ContainsText("Configuration: guitool.review.cmd") &&
                    snapshot.ContainsText("Scope: Repository") &&
                    snapshot.ContainsText("Shell involved: yes") &&
                    snapshot.ContainsText("Data exposed: configured tool name"),
                TimeSpan.FromSeconds(3),
                "Capability review presents exact source, execution boundary, and exposed data");
            await automator.ClickAtAsync(0, 0, MouseButton.Left, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => session.RunConfiguredToolCallCount == 1 &&
                    !snapshot.ContainsText("Configured command security review") &&
                    !snapshot.ContainsText("Tool output: Review changes"),
                TimeSpan.FromSeconds(3),
                "Clicking outside denies without displaying tool output");

            await OpenConfiguredToolPreflightAsync(automator, timeout.Token);
            using (var preflight = automator.CreateSnapshot())
            {
                var continueButton = FindText(preflight, "Continue to security review");
                await automator.ClickAtAsync(
                    continueButton.X + 2,
                    continueButton.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilTextAsync(
                "Configured command security review",
                TimeSpan.FromSeconds(3));
            using (var review = automator.CreateSnapshot())
            {
                var allowOnce = FindText(review, "Allow once");
                await automator.ClickAtAsync(
                    allowOnce.X + 2,
                    allowOnce.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilAsync(
                snapshot => session.RunConfiguredToolCallCount == 2 &&
                    snapshot.ContainsText("Tool output: Review changes") &&
                    snapshot.ContainsText("fake configured tool output") &&
                    snapshot.ContainsText("Completed successfully"),
                TimeSpan.FromSeconds(3),
                "Allow once runs the exact fake tool and presents bounded output");
            Assert.AreEqual("review.txt", session.LastConfiguredToolInvocation?.FocusedPath?.DisplayText);
            await automator.ClickAtAsync(0, 0, MouseButton.Left, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Tool output: Review changes"),
                TimeSpan.FromSeconds(3),
                "Clicking outside closes configured-tool output");

            session.ConfigureConfiguration(
                CreateConfigurationEntry("guitool.review.cmd", "printf 'review output'"),
                CreateConfigurationEntry("guitool.review.title", "Review changes"));
            await OpenConfiguredToolCommandAsync(automator, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => snapshot.ContainsText("Configured command security review") &&
                    !snapshot.ContainsText("Configured tool: Review changes"),
                TimeSpan.FromSeconds(3),
                "A tool without Git prompt options proceeds directly to its required security review");
            await automator.KeyAsync(Hex1bKey.Escape, timeout.Token);
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    private static async Task OpenConfiguredToolPreflightAsync(
        Hex1bTerminalAutomator automator,
        CancellationToken cancellationToken)
    {
        await OpenConfiguredToolCommandAsync(automator, cancellationToken);
        await automator.WaitUntilTextAsync("Configured tool: Review changes", TimeSpan.FromSeconds(3));
    }

    private static async Task OpenConfiguredToolCommandAsync(
        Hex1bTerminalAutomator automator,
        CancellationToken cancellationToken)
    {
        await automator.KeyAsync(Hex1bKey.F2, cancellationToken);
        await automator.WaitUntilTextAsync("Command palette", TimeSpan.FromSeconds(3));
        using (var palette = automator.CreateSnapshot())
        {
            var filter = FindText(palette, "Find action:");
            await automator.ClickAtAsync(
                filter.X + 14,
                filter.Y,
                MouseButton.Left,
                cancellationToken);
        }

        await automator.TypeAsync("review changes", cancellationToken);
        await automator.WaitUntilTextAsync("Tools: Review changes", TimeSpan.FromSeconds(3));
        await automator.KeyAsync(Hex1bKey.Enter, cancellationToken);
    }

    private static GitConfigurationEntry CreateConfigurationEntry(string key, string value)
        => new(
            GitConfigurationScope.Local,
            GitConfigurationOrigin.FromBytes("file:.git/config"u8),
            GitConfigurationKey.FromBytes(System.Text.Encoding.UTF8.GetBytes(key)),
            GitConfigurationValue.FromBytes(System.Text.Encoding.UTF8.GetBytes(value)));

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
}
