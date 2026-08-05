using GitSail.Git.Execution;
using GitSail.Ui;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;
using System.Text;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies repository chooser rendering and complete pointer-driven local workflows against real Git.
/// </summary>
[TestClass]
public sealed class RepositoryChooserViewTests
{
    /// <summary>
    /// Verifies all chooser workflows remain visible and pointer-activatable at eighty columns by twenty-four rows.
    /// </summary>
    [TestMethod]
    public async Task Chooser_AtEightyByTwentyFour_NavigatesEveryRepositoryWorkflowWithMouse()
    {
        var temporaryDirectory = CreateTemporaryDirectory();
        try
        {
            var processEnvironment = CreateProcessEnvironment(temporaryDirectory);
            using var session = await RepositoryChooserSession.CreateAsync(
                CanonicalDirectory.Create(temporaryDirectory),
                processEnvironment,
                "Choose a repository workflow.",
                TestContext.Current!.CancellationToken);
            var view = new RepositoryChooserView(session, TestContext.Current.CancellationToken);
            Hex1bApp? application = null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            await using var terminal = Hex1bTerminal.CreateBuilder()
                .WithHeadless()
                .WithDimensions(80, 24)
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
            var automator = new Hex1bTerminalAutomator(terminal, TimeSpan.FromSeconds(10));

            try
            {
                await automator.WaitUntilTextAsync("repository chooser", TimeSpan.FromSeconds(10));
                using (var initial = automator.CreateSnapshot())
                {
                    Assert.IsTrue(initial.ContainsText("[Open]"));
                    Assert.IsTrue(initial.ContainsText("Recent"));
                    Assert.IsTrue(initial.ContainsText("Clone"));
                    Assert.IsTrue(initial.ContainsText("Initialize"));
                    Assert.IsTrue(initial.ContainsText("Initialize bare"));
                    Assert.IsTrue(initial.ContainsText("Open worktree"));
                    var clone = FindText(initial, "Clone");
                    await automator.ClickAtAsync(clone.X + 1, clone.Y, MouseButton.Left, timeout.Token);
                }

                await automator.WaitUntilTextAsync("Mode: Standard", TimeSpan.FromSeconds(10));
                using (var standard = automator.CreateSnapshot())
                {
                    var mode = FindText(standard, "Mode: Standard");
                    await automator.ClickAtAsync(mode.X + 1, mode.Y, MouseButton.Left, timeout.Token);
                }

                await automator.WaitUntilTextAsync("Full copy disables local hardlinks", TimeSpan.FromSeconds(10));
                using (var fullCopy = automator.CreateSnapshot())
                {
                    var mode = FindText(fullCopy, "Mode: Full copy");
                    await automator.ClickAtAsync(mode.X + 1, mode.Y, MouseButton.Left, timeout.Token);
                }

                await automator.WaitUntilTextAsync("Shared clone can become corrupt", TimeSpan.FromSeconds(10));
                using (var shared = automator.CreateSnapshot())
                {
                    var recursive = FindText(shared, "[ ] Recursive submodules");
                    await automator.ClickAtAsync(
                        recursive.X + 1,
                        recursive.Y,
                        MouseButton.Left,
                        timeout.Token);
                }

                await automator.WaitUntilTextAsync("[x] Recursive submodules", TimeSpan.FromSeconds(10));
                using (var recursive = automator.CreateSnapshot())
                {
                    var recent = FindText(recursive, "Recent");
                    await automator.ClickAtAsync(recent.X + 1, recent.Y, MouseButton.Left, timeout.Token);
                }

                await automator.WaitUntilTextAsync("Recent repositories (0)", TimeSpan.FromSeconds(10));
                using (var final = automator.CreateSnapshot())
                {
                    Assert.IsTrue(final.ContainsText("No recent repositories are recorded."));
                    Assert.IsTrue(final.ContainsText("Mouse"));
                }
            }
            finally
            {
                application?.RequestStop();
                await runTask;
                view.Detach();
            }
        }
        finally
        {
            TestDirectory.Delete(temporaryDirectory);
        }
    }

    /// <summary>
    /// Verifies chooser help is completely framed, scrollable, and dismissible at sixty columns by eighteen rows.
    /// </summary>
    [TestMethod]
    public async Task ChooserHelp_AtSixtyByEighteen_FitsScrollsAndCloses()
    {
        var temporaryDirectory = CreateTemporaryDirectory();
        try
        {
            var processEnvironment = CreateProcessEnvironment(temporaryDirectory);
            using var session = await RepositoryChooserSession.CreateAsync(
                CanonicalDirectory.Create(temporaryDirectory),
                processEnvironment,
                "Choose a repository workflow.",
                TestContext.Current!.CancellationToken);
            var view = new RepositoryChooserView(session, TestContext.Current.CancellationToken);
            Hex1bApp? application = null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
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
            var automator = new Hex1bTerminalAutomator(terminal, TimeSpan.FromSeconds(10));

            try
            {
                await automator.WaitUntilTextAsync("repository chooser", TimeSpan.FromSeconds(10));
                await automator.KeyAsync(Hex1bKey.F1, timeout.Token);
                await automator.WaitUntilTextAsync("Repository chooser help", TimeSpan.FromSeconds(10));
                using (var help = automator.CreateSnapshot())
                {
                    AssertWindowFrameIsComplete(help, "Repository chooser help", 58, 16);
                    Assert.IsTrue(help.ContainsText("Open accepts a repository root"));
                }

                await automator.ScrollDownAsync(8, timeout.Token);
                await automator.WaitUntilTextAsync("Failed new targets", TimeSpan.FromSeconds(10));
                await automator.KeyAsync(Hex1bKey.Escape, timeout.Token);
                await automator.WaitUntilAsync(
                    snapshot => !snapshot.ContainsText("Repository chooser help"),
                    TimeSpan.FromSeconds(10),
                    "Escape closes compact chooser help after scrolling");
            }
            finally
            {
                application?.RequestStop();
                await runTask;
                view.Detach();
            }
        }
        finally
        {
            TestDirectory.Delete(temporaryDirectory);
        }
    }

    /// <summary>
    /// Verifies pointer focus and typed source and target paths complete a real local clone and select it for opening.
    /// </summary>
    [TestMethod]
    public async Task Clone_WithMouseAndKeyboard_CreatesAndSelectsRepository()
    {
        var temporaryDirectory = CreateTemporaryDirectory();
        try
        {
            var processEnvironment = CreateProcessEnvironment(temporaryDirectory);
            var installation = await new GitVersionService(
                new ExecutableResolver(processEnvironment),
                new ChildProcessRunner()).GetAsync(
                CanonicalDirectory.Create(temporaryDirectory),
                TestContext.Current!.CancellationToken);
            var sourcePath = await CreateRepositoryAsync(
                temporaryDirectory,
                "chooser clone source",
                installation,
                processEnvironment);
            var targetPath = Path.Combine(temporaryDirectory, "chooser clone target");
            using var session = await RepositoryChooserSession.CreateAsync(
                CanonicalDirectory.Create(temporaryDirectory),
                processEnvironment,
                "Clone a repository.",
                TestContext.Current.CancellationToken);
            var view = new RepositoryChooserView(session, TestContext.Current.CancellationToken);
            Hex1bApp? application = null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
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
            var automator = new Hex1bTerminalAutomator(terminal, TimeSpan.FromSeconds(10));

            try
            {
                await automator.WaitUntilTextAsync("repository chooser", TimeSpan.FromSeconds(10));
                using (var initial = automator.CreateSnapshot())
                {
                    var clone = FindText(initial, "Clone");
                    await automator.ClickAtAsync(clone.X + 1, clone.Y, MouseButton.Left, timeout.Token);
                }

                await automator.WaitUntilTextAsync("Source:", TimeSpan.FromSeconds(10));
                using (var clonePage = automator.CreateSnapshot())
                {
                    var source = FindText(clonePage, "Source: ");
                    await automator.ClickAtAsync(source.X + "Source: ".Length + 1, source.Y, MouseButton.Left, timeout.Token);
                }

                await automator.TypeAsync(sourcePath, timeout.Token);
                using (var sourceEntered = automator.CreateSnapshot())
                {
                    var target = FindText(sourceEntered, "Target: ");
                    await automator.ClickAtAsync(target.X + "Target: ".Length + 1, target.Y, MouseButton.Left, timeout.Token);
                }

                await new Hex1bTerminalInputSequenceBuilder()
                    .Ctrl()
                    .Key(Hex1bKey.A)
                    .Key(Hex1bKey.Backspace)
                    .Build()
                    .ApplyAsync(terminal, timeout.Token);
                await automator.TypeAsync(targetPath, timeout.Token);
                using (var ready = automator.CreateSnapshot())
                {
                    var clone = FindText(ready, "Clone and open");
                    await automator.ClickAtAsync(clone.X + 1, clone.Y, MouseButton.Left, timeout.Token);
                }

                await automator.WaitUntilAsync(
                    _ => session.SelectedDirectory is not null,
                    TimeSpan.FromSeconds(20),
                    "The completed clone is selected for opening");
                await runTask;
                Assert.IsTrue(Directory.Exists(Path.Combine(targetPath, ".git")));
                Assert.AreEqual("chooser content\n", await File.ReadAllTextAsync(
                    Path.Combine(targetPath, "tracked.txt"),
                    TestContext.Current.CancellationToken));
            }
            finally
            {
                application?.RequestStop();
                if (!runTask.IsCompleted)
                {
                    await runTask;
                }

                view.Detach();
            }
        }
        finally
        {
            TestDirectory.Delete(temporaryDirectory);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gitsail-chooser-view-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static TestProcessEnvironment CreateProcessEnvironment(string homeDirectory)
        => new(new Dictionary<string, string?>
        {
            ["HOME"] = homeDirectory,
            ["USERPROFILE"] = homeDirectory,
            ["XDG_CONFIG_HOME"] = Path.Combine(homeDirectory, "xdg-config"),
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["PATH"] = Environment.GetEnvironmentVariable("PATH"),
            ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot"),
            ["WINDIR"] = Environment.GetEnvironmentVariable("WINDIR"),
        });

    private static async Task<string> CreateRepositoryAsync(
        string parentDirectory,
        string name,
        GitInstallation installation,
        IProcessEnvironment processEnvironment)
    {
        var repositoryPath = Path.Combine(parentDirectory, name);
        var runner = new ChildProcessRunner();
        var environmentFactory = new GitChildEnvironmentFactory(processEnvironment);
        await RunGitAsync(
            parentDirectory,
            installation,
            runner,
            environmentFactory,
            "init",
            "--quiet",
            "--initial-branch=main",
            "--",
            repositoryPath);
        await File.WriteAllTextAsync(
            Path.Combine(repositoryPath, "tracked.txt"),
            "chooser content\n",
            TestContext.Current!.CancellationToken);
        await RunGitAsync(
            repositoryPath,
            installation,
            runner,
            environmentFactory,
            "add",
            "--",
            "tracked.txt");
        await RunGitAsync(
            repositoryPath,
            installation,
            runner,
            environmentFactory,
            "-c",
            "user.name=GitSail Tests",
            "-c",
            "user.email=gitsail@example.invalid",
            "commit",
            "--quiet",
            "--no-gpg-sign",
            "--message=initial");
        return repositoryPath;
    }

    private static async Task RunGitAsync(
        string workingDirectory,
        GitInstallation installation,
        ChildProcessRunner runner,
        GitChildEnvironmentFactory environmentFactory,
        params string[] arguments)
    {
        var result = await runner.RunAsync(
            new ProcessInvocation(
                installation.Executable,
                [.. arguments.Select(ProcessArgument.Literal)],
                CanonicalDirectory.Create(workingDirectory),
                environmentFactory.CreateCheckoutEnvironment(),
                StandardInputSource.Empty(),
                OutputPolicy.Create(4 * 1024 * 1024, 4 * 1024 * 1024)),
            TestContext.Current!.CancellationToken);
        Assert.AreEqual(0, result.ExitCode, Encoding.UTF8.GetString(result.StandardError.Span));
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
