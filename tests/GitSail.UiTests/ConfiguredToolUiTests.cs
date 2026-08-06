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
