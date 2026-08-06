using GitSail.CommandLine;
using GitSail.Domain;
using GitSail.Ui;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;

namespace GitSail.UiTests;

/// <summary>
/// Verifies SSH key creation is mouse-operable, reviewable, and secret-free inside the TUI.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SshKeyCreationUiTests
{
    /// <summary>
    /// Verifies algorithm selection, overwrite review, Escape, and terminal handoff with the mouse.
    /// </summary>
    [TestMethod]
    public async Task SshKeyCreation_WithMouseAndEscape_RequestsExactReviewedTerminalOperation()
    {
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"gitsail-ssh-key-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        var keyPath = Path.Combine(temporaryDirectory, "reviewed key");
        await File.WriteAllTextAsync(
            keyPath,
            "existing private key",
            TestContext.Current!.CancellationToken);
        var session = new FakeRepositoryWorkspaceSession();
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
            await OpenSshKeyCreationAsync(automator, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => snapshot.ContainsText("Create SSH key") &&
                    snapshot.ContainsText("Algorithm: Ed25519") &&
                    snapshot.ContainsText("id_ed25519") &&
                    snapshot.ContainsText("GitSail never receives"),
                TimeSpan.FromSeconds(3),
                "SSH key creation opens with secure Ed25519 defaults");
            await automator.KeyAsync(Hex1bKey.Escape, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Create SSH key"),
                TimeSpan.FromSeconds(3),
                "One Escape closes SSH key creation");

            await OpenSshKeyCreationAsync(automator, timeout.Token);
            await automator.WaitUntilTextAsync("Algorithm: Ed25519", TimeSpan.FromSeconds(3));
            using (var editor = automator.CreateSnapshot())
            {
                var algorithm = FindText(editor, "Algorithm: Ed25519");
                await automator.ClickAtAsync(
                    algorithm.X + 4,
                    algorithm.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilAsync(
                snapshot => snapshot.ContainsText("Algorithm: RSA 4096") &&
                    snapshot.ContainsText("id_rsa"),
                TimeSpan.FromSeconds(3),
                "Pointer selection changes the algorithm and conventional default path");
            using (var editor = automator.CreateSnapshot())
            {
                var pathLabel = FindText(editor, "Private-key output path (fully qualified):");
                await automator.ClickAtAsync(
                    pathLabel.X + 2,
                    pathLabel.Y + 1,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.KeyAsync(Hex1bKey.A, Hex1bModifiers.Control, timeout.Token);
            await automator.TypeAsync(keyPath, timeout.Token);
            using (var editor = automator.CreateSnapshot())
            {
                var commentLabel = FindText(editor, "Public-key comment (optional, one line):");
                await automator.ClickAtAsync(
                    commentLabel.X + 2,
                    commentLabel.Y + 1,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.TypeAsync("developer@example.invalid", timeout.Token);
            await automator.WaitUntilTextAsync("Review replacement...", TimeSpan.FromSeconds(3));
            using (var editor = automator.CreateSnapshot())
            {
                var review = FindText(editor, "Review replacement...");
                await automator.ClickAtAsync(
                    review.X + 2,
                    review.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilAsync(
                snapshot => snapshot.ContainsText("Replace existing SSH key?") &&
                    snapshot.ContainsText("Algorithm: RSA 4096") &&
                    snapshot.ContainsText("developer@example.invalid") &&
                    snapshot.ContainsText("Continue to overwrite prompt"),
                TimeSpan.FromSeconds(3),
                "Replacement review presents the exact nonsecret request");
            await automator.KeyAsync(Hex1bKey.Escape, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Replace existing SSH key?") &&
                    snapshot.ContainsText("Review replacement..."),
                TimeSpan.FromSeconds(3),
                "One Escape closes only the replacement review");
            Assert.IsNull(session.RequestedSshKeyCreation);

            using (var editor = automator.CreateSnapshot())
            {
                var review = FindText(editor, "Review replacement...");
                await automator.ClickAtAsync(
                    review.X + 2,
                    review.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilTextAsync("Continue to overwrite prompt", TimeSpan.FromSeconds(3));
            using (var confirmation = automator.CreateSnapshot())
            {
                var create = FindText(confirmation, "Continue to overwrite prompt");
                await automator.ClickAtAsync(
                    create.X + 2,
                    create.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await runTask;
            Assert.IsNotNull(session.RequestedSshKeyCreation);
            Assert.AreEqual(SshKeyAlgorithm.Rsa4096, session.RequestedSshKeyCreation.Algorithm);
            Assert.AreEqual(Path.GetFullPath(keyPath), session.RequestedSshKeyCreation.FilePath);
            Assert.AreEqual("developer@example.invalid", session.RequestedSshKeyCreation.Comment);
            Assert.IsTrue(session.RequestedSshKeyCreation.ReplaceExisting);
            Assert.AreEqual(
                "existing private key",
                await File.ReadAllTextAsync(keyPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static async Task OpenSshKeyCreationAsync(
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

        await automator.TypeAsync("create ssh key", cancellationToken);
        await automator.WaitUntilTextAsync("Tools: Create SSH key...", TimeSpan.FromSeconds(3));
        await automator.KeyAsync(Hex1bKey.Enter, cancellationToken);
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
}
