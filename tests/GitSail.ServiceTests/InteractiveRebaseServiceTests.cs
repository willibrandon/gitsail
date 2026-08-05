using GitSail.CommandLine;
using GitSail.Domain;
using GitSail.Git.Execution;
using GitSail.Ui;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;
using System.Text;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies interactive-rebase planning, execution, and recovery against real repositories.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class InteractiveRebaseServiceTests
{
    private string? _temporaryDirectory;
    private GitInstallation? _installation;
    private ChildProcessRunner? _runner;
    private RepositoryMutationCoordinator? _coordinator;
    private GitChildEnvironmentFactory? _environmentFactory;
    private InteractiveRebaseService? _service;

    /// <summary>
    /// Creates an isolated home and resolves Git for each interactive-rebase test.
    /// </summary>
    [TestInitialize]
    public async Task InitializeAsync()
    {
        _temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"gitsail-rebase-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);
        _runner = new ChildProcessRunner();
        _coordinator = new RepositoryMutationCoordinator();
        _environmentFactory = TestProcessEnvironment.CreateGitFactory(_temporaryDirectory);
        _installation = await new GitVersionService(
            new ExecutableResolver(new RuntimeProcessEnvironment()),
            _runner).GetAsync(
                CanonicalDirectory.Create(_temporaryDirectory),
                TestContext.Current!.CancellationToken);
        _service = new InteractiveRebaseService(
            _installation,
            _runner,
            new TerminalChildProcessRunner(),
            _environmentFactory,
            _coordinator,
            ":",
            TimeProvider.System);
    }

    /// <summary>
    /// Removes the isolated repository and mutation coordinator after each test.
    /// </summary>
    [TestCleanup]
    public void Cleanup()
    {
        _coordinator?.Dispose();
        if (_temporaryDirectory is not null && Directory.Exists(_temporaryDirectory))
        {
            TestDirectory.Delete(_temporaryDirectory);
        }
    }

    /// <summary>
    /// Verifies a confirmed plan rebases every selected commit onto the exact new base.
    /// </summary>
    [TestMethod]
    public async Task StartAsync_WithExactPlan_RewritesSelectedCommitsOntoNewBase()
    {
        var setup = await CreateNonConflictingFixtureAsync("complete");
        var plan = await _service!.PrepareAsync(
            setup.WorkingDirectory,
            new RebaseOptions(setup.Base.ToString(), setup.Main.ToString()),
            TestContext.Current!.CancellationToken);

        var result = await _service.StartAsync(
            setup.WorkingDirectory,
            plan,
            TestContext.Current.CancellationToken);

        Assert.AreEqual(2, plan.CommitCount);
        Assert.AreEqual(RebaseOutcome.Completed, result.Outcome);
        Assert.IsNull(result.State);
        Assert.AreEqual(
            setup.Main,
            await ReadObjectIdAsync(setup.RepositoryPath, "HEAD~2"));
        Assert.AreEqual("feature two", await ReadTextAsync(
            setup.RepositoryPath,
            "log",
            "-1",
            "--format=%s"));
        Assert.IsNull(await _service.CaptureStateAsync(
            setup.WorkingDirectory,
            TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies a conflicting rebase exposes exact stopped state and abort restores the original branch.
    /// </summary>
    [TestMethod]
    public async Task StartAsync_WithConflict_StopsAndAbortRestoresOriginalHead()
    {
        var setup = await CreateConflictingFixtureAsync("abort");
        var plan = await _service!.PrepareAsync(
            setup.WorkingDirectory,
            new RebaseOptions(setup.Base.ToString(), setup.Main.ToString()),
            TestContext.Current!.CancellationToken);

        var stopped = await _service.StartAsync(
            setup.WorkingDirectory,
            plan,
            TestContext.Current.CancellationToken);

        Assert.AreEqual(RebaseOutcome.Stopped, stopped.Outcome);
        Assert.IsNotNull(stopped.State);
        Assert.IsNotNull(stopped.State.CurrentCommit);
        Assert.AreEqual(setup.Feature, stopped.State.CurrentCommit);
        var aborted = await _service.ControlAsync(
            setup.WorkingDirectory,
            stopped.State,
            RebaseControl.Abort,
            TestContext.Current.CancellationToken);
        Assert.AreEqual(RebaseOutcome.Completed, aborted.Outcome);
        Assert.AreEqual(setup.Feature, await ReadObjectIdAsync(setup.RepositoryPath, "HEAD"));
        Assert.AreEqual("feature\n", File.ReadAllText(Path.Combine(setup.RepositoryPath, "tracked.txt")));
    }

    /// <summary>
    /// Verifies resolving and continuing a stopped rebase completes the rewritten commit.
    /// </summary>
    [TestMethod]
    public async Task ControlAsync_AfterConflictResolution_ContinuesRebase()
    {
        var setup = await CreateConflictingFixtureAsync("continue");
        var plan = await _service!.PrepareAsync(
            setup.WorkingDirectory,
            new RebaseOptions(setup.Base.ToString(), setup.Main.ToString()),
            TestContext.Current!.CancellationToken);
        var stopped = await _service.StartAsync(
            setup.WorkingDirectory,
            plan,
            TestContext.Current.CancellationToken);
        Assert.IsNotNull(stopped.State);
        await File.WriteAllTextAsync(
            Path.Combine(setup.RepositoryPath, "tracked.txt"),
            "resolved\n",
            TestContext.Current.CancellationToken);
        await RunGitAsync(setup.RepositoryPath, "add", "--", "tracked.txt");

        var completed = await _service.ControlAsync(
            setup.WorkingDirectory,
            stopped.State,
            RebaseControl.Continue,
            TestContext.Current.CancellationToken);

        Assert.AreEqual(RebaseOutcome.Completed, completed.Outcome);
        Assert.IsNull(completed.State);
        Assert.AreEqual("resolved\n", File.ReadAllText(Path.Combine(setup.RepositoryPath, "tracked.txt")));
        Assert.AreEqual(setup.Main, await ReadObjectIdAsync(setup.RepositoryPath, "HEAD^"));
    }

    /// <summary>
    /// Verifies skipping a conflicting todo commit advances and clears the rebase transaction.
    /// </summary>
    [TestMethod]
    public async Task ControlAsync_WithSkip_DiscardsCurrentCommit()
    {
        var setup = await CreateConflictingFixtureAsync("skip");
        var plan = await _service!.PrepareAsync(
            setup.WorkingDirectory,
            new RebaseOptions(setup.Base.ToString(), setup.Main.ToString()),
            TestContext.Current!.CancellationToken);
        var stopped = await _service.StartAsync(
            setup.WorkingDirectory,
            plan,
            TestContext.Current.CancellationToken);
        Assert.IsNotNull(stopped.State);

        var completed = await _service.ControlAsync(
            setup.WorkingDirectory,
            stopped.State,
            RebaseControl.Skip,
            TestContext.Current.CancellationToken);

        Assert.AreEqual(RebaseOutcome.Completed, completed.Outcome);
        Assert.AreEqual(setup.Main, await ReadObjectIdAsync(setup.RepositoryPath, "HEAD"));
        Assert.AreEqual("main\n", File.ReadAllText(Path.Combine(setup.RepositoryPath, "tracked.txt")));
    }

    /// <summary>
    /// Verifies editing an active todo returns to the same recoverable rebase transaction.
    /// </summary>
    [TestMethod]
    public async Task ControlAsync_WithEditTodo_RetainsStoppedRebaseState()
    {
        var setup = await CreateConflictingFixtureAsync("edit-todo");
        var plan = await _service!.PrepareAsync(
            setup.WorkingDirectory,
            new RebaseOptions(setup.Base.ToString(), setup.Main.ToString()),
            TestContext.Current!.CancellationToken);
        var stopped = await _service.StartAsync(
            setup.WorkingDirectory,
            plan,
            TestContext.Current.CancellationToken);
        Assert.IsNotNull(stopped.State);
        Assert.IsTrue(stopped.State.CanEditTodo);

        var edited = await _service.ControlAsync(
            setup.WorkingDirectory,
            stopped.State,
            RebaseControl.EditTodo,
            TestContext.Current.CancellationToken);

        Assert.AreEqual(RebaseOutcome.Stopped, edited.Outcome);
        Assert.IsNotNull(edited.State);
        Assert.AreEqual(stopped.State.CurrentCommit, edited.State.CurrentCommit);
        _ = await _service.ControlAsync(
            setup.WorkingDirectory,
            edited.State,
            RebaseControl.Abort,
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies a changed worktree invalidates the exact plan before Git creates sequencer state.
    /// </summary>
    [TestMethod]
    public async Task StartAsync_AfterWorktreeChange_RejectsStaleConfirmation()
    {
        var setup = await CreateNonConflictingFixtureAsync("stale");
        var plan = await _service!.PrepareAsync(
            setup.WorkingDirectory,
            new RebaseOptions(setup.Base.ToString(), setup.Main.ToString()),
            TestContext.Current!.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(setup.RepositoryPath, "feature-two.txt"),
            "changed after confirmation\n",
            TestContext.Current.CancellationToken);

        await Assert.ThrowsExactlyAsync<RepositoryPreconditionException>(() =>
            _service.StartAsync(
                setup.WorkingDirectory,
                plan,
                TestContext.Current.CancellationToken));

        Assert.IsNull(await _service.CaptureStateAsync(
            setup.WorkingDirectory,
            TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies the minimum-size planning UI shows a complete plan and dismisses confirmation by pointer or Escape.
    /// </summary>
    [TestMethod]
    public async Task RebaseView_WithPreparedPlan_SupportsCompactPointerAndEscapeCancellation()
    {
        var setup = await CreateNonConflictingFixtureAsync("prepared-view");
        using var session = await RebaseSession.OpenAsync(
            setup.WorkingDirectory,
            new RebaseOptions(setup.Base.ToString(), setup.Main.ToString()),
            CreateProcessEnvironment(),
            TestContext.Current!.CancellationToken);
        await session.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.IsNotNull(session.Plan);
        var view = new RebaseView(session, TestContext.Current.CancellationToken);
        Hex1bApp? application = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
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
        var automator = new Hex1bTerminalAutomator(terminal, TimeSpan.FromSeconds(5));

        try
        {
            await automator.WaitUntilTextAsync("Start rebase...", TimeSpan.FromSeconds(5));
            using (var compact = automator.CreateSnapshot())
            {
                Assert.IsTrue(compact.ContainsText($"Git {_installation!.Version}"));
                Assert.IsTrue(compact.ContainsText("Commits to rewrite: 2"));
                Assert.IsFalse(compact.ContainsText("More room needed"));
                var start = FindText(compact, "Start rebase...");
                await automator.ClickAtAsync(start.X + 1, start.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Start interactive rebase?", TimeSpan.FromSeconds(5));
            using (var confirmation = automator.CreateSnapshot())
            {
                AssertWindowFrameIsComplete(confirmation, "Start interactive rebase?", 58, 12);
            }

            await automator.ClickAtAsync(0, 1, MouseButton.Left, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Start interactive rebase?"),
                TimeSpan.FromSeconds(5),
                "Pointer click-away closes the start confirmation");
            using (var reopened = automator.CreateSnapshot())
            {
                var start = FindText(reopened, "Start rebase...");
                await automator.ClickAtAsync(start.X + 1, start.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Start interactive rebase?", TimeSpan.FromSeconds(5));
            await automator.KeyAsync(Hex1bKey.Escape, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Start interactive rebase?"),
                TimeSpan.FromSeconds(5),
                "Escape closes the start confirmation");
            Assert.IsNull(session.RequestedAction);
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies stopped recovery exposes files and every Git control while confirmations dismiss safely.
    /// </summary>
    [TestMethod]
    public async Task RebaseView_WithStoppedConflict_ShowsRecoveryAndDismissesAbortConfirmation()
    {
        var setup = await CreateConflictingFixtureAsync("stopped-view");
        var plan = await _service!.PrepareAsync(
            setup.WorkingDirectory,
            new RebaseOptions(setup.Base.ToString(), setup.Main.ToString()),
            TestContext.Current!.CancellationToken);
        var stopped = await _service.StartAsync(
            setup.WorkingDirectory,
            plan,
            TestContext.Current.CancellationToken);
        Assert.IsNotNull(stopped.State);
        using var session = await RebaseSession.OpenAsync(
            setup.WorkingDirectory,
            new RebaseOptions(Upstream: null, Onto: null),
            CreateProcessEnvironment(),
            TestContext.Current.CancellationToken);
        await session.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.IsNotNull(session.State);
        var view = new RebaseView(session, TestContext.Current.CancellationToken);
        Hex1bApp? application = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
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
        var automator = new Hex1bTerminalAutomator(terminal, TimeSpan.FromSeconds(5));

        try
        {
            await automator.WaitUntilTextAsync("Resolve files", TimeSpan.FromSeconds(5));
            using (var recovery = automator.CreateSnapshot())
            {
                Assert.IsTrue(recovery.ContainsText("Continue"));
                Assert.IsTrue(recovery.ContainsText("Edit todo"));
                Assert.IsTrue(recovery.ContainsText("Skip..."));
                var abort = FindText(recovery, "Abort...");
                await automator.ClickAtAsync(abort.X + 1, abort.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Abort this rebase?", TimeSpan.FromSeconds(5));
            using (var confirmation = automator.CreateSnapshot())
            {
                AssertWindowFrameIsComplete(confirmation, "Abort this rebase?", 58, 10);
            }

            await automator.ClickAtAsync(0, 1, MouseButton.Left, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Abort this rebase?"),
                TimeSpan.FromSeconds(5),
                "Pointer click-away closes the abort confirmation");
            using (var recovery = automator.CreateSnapshot())
            {
                var abort = FindText(recovery, "Abort...");
                await automator.ClickAtAsync(abort.X + 1, abort.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilTextAsync("Abort this rebase?", TimeSpan.FromSeconds(5));
            await automator.KeyAsync(Hex1bKey.Escape, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Abort this rebase?"),
                TimeSpan.FromSeconds(5),
                "Escape closes the abort confirmation");
            Assert.IsNull(session.RequestedAction);
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }

        var liveState = await _service.CaptureStateAsync(
            setup.WorkingDirectory,
            TestContext.Current.CancellationToken);
        Assert.IsNotNull(liveState);
        _ = await _service.ControlAsync(
            setup.WorkingDirectory,
            liveState,
            RebaseControl.Abort,
            TestContext.Current.CancellationToken);
    }

    private async Task<(
        string RepositoryPath,
        CanonicalDirectory WorkingDirectory,
        ObjectId Base,
        ObjectId Main)> CreateNonConflictingFixtureAsync(string name)
    {
        var repositoryPath = await CreateBaseRepositoryAsync(name);
        var baseCommit = await ReadObjectIdAsync(repositoryPath, "HEAD");
        await RunGitAsync(repositoryPath, "switch", "--quiet", "--create", "feature");
        await File.WriteAllTextAsync(
            Path.Combine(repositoryPath, "feature-one.txt"),
            "one\n",
            TestContext.Current!.CancellationToken);
        await RunGitAsync(repositoryPath, "add", "--", "feature-one.txt");
        await CommitAsync(repositoryPath, "feature one");
        await File.WriteAllTextAsync(
            Path.Combine(repositoryPath, "feature-two.txt"),
            "two\n",
            TestContext.Current.CancellationToken);
        await RunGitAsync(repositoryPath, "add", "--", "feature-two.txt");
        await CommitAsync(repositoryPath, "feature two");
        await RunGitAsync(repositoryPath, "switch", "--quiet", "main");
        await File.WriteAllTextAsync(
            Path.Combine(repositoryPath, "main.txt"),
            "main\n",
            TestContext.Current.CancellationToken);
        await RunGitAsync(repositoryPath, "add", "--", "main.txt");
        await CommitAsync(repositoryPath, "main advance");
        var main = await ReadObjectIdAsync(repositoryPath, "HEAD");
        await RunGitAsync(repositoryPath, "switch", "--quiet", "feature");
        return (repositoryPath, CanonicalDirectory.Create(repositoryPath), baseCommit, main);
    }

    private async Task<(
        string RepositoryPath,
        CanonicalDirectory WorkingDirectory,
        ObjectId Base,
        ObjectId Main,
        ObjectId Feature)> CreateConflictingFixtureAsync(string name)
    {
        var repositoryPath = await CreateBaseRepositoryAsync(name);
        var baseCommit = await ReadObjectIdAsync(repositoryPath, "HEAD");
        await RunGitAsync(repositoryPath, "switch", "--quiet", "--create", "feature");
        await File.WriteAllTextAsync(
            Path.Combine(repositoryPath, "tracked.txt"),
            "feature\n",
            TestContext.Current!.CancellationToken);
        await RunGitAsync(repositoryPath, "add", "--", "tracked.txt");
        await CommitAsync(repositoryPath, "feature edit");
        var feature = await ReadObjectIdAsync(repositoryPath, "HEAD");
        await RunGitAsync(repositoryPath, "switch", "--quiet", "main");
        await File.WriteAllTextAsync(
            Path.Combine(repositoryPath, "tracked.txt"),
            "main\n",
            TestContext.Current.CancellationToken);
        await RunGitAsync(repositoryPath, "add", "--", "tracked.txt");
        await CommitAsync(repositoryPath, "main edit");
        var main = await ReadObjectIdAsync(repositoryPath, "HEAD");
        await RunGitAsync(repositoryPath, "switch", "--quiet", "feature");
        return (repositoryPath, CanonicalDirectory.Create(repositoryPath), baseCommit, main, feature);
    }

    private async Task<string> CreateBaseRepositoryAsync(string name)
    {
        var repositoryPath = Path.Combine(_temporaryDirectory!, name);
        Directory.CreateDirectory(repositoryPath);
        await RunGitAsync(repositoryPath, "init", "--quiet", "--initial-branch=main");
        await File.WriteAllTextAsync(
            Path.Combine(repositoryPath, "tracked.txt"),
            "base\n",
            TestContext.Current!.CancellationToken);
        await RunGitAsync(repositoryPath, "add", "--", "tracked.txt");
        await CommitAsync(repositoryPath, "base");
        return repositoryPath;
    }

    private Task CommitAsync(string repositoryPath, string message)
        => RunGitAsync(
            repositoryPath,
            "commit",
            "--quiet",
            "--no-gpg-sign",
            $"--message={message}");

    private async Task<ObjectId> ReadObjectIdAsync(string repositoryPath, string revision)
    {
        var output = await RunGitForOutputAsync(repositoryPath, "rev-parse", "--verify", revision);
        Assert.IsTrue(ObjectId.TryParseHex(output, out var objectId));
        Assert.IsNotNull(objectId);
        return objectId;
    }

    private async Task<string> ReadTextAsync(string repositoryPath, params string[] arguments)
        => Encoding.UTF8.GetString(await RunGitForOutputAsync(repositoryPath, arguments)).Trim();

    private async Task RunGitAsync(string repositoryPath, params string[] arguments)
    {
        var result = await RunGitForResultAsync(repositoryPath, arguments);
        Assert.AreEqual(0, result.ExitCode, Encoding.UTF8.GetString(result.StandardError.Span));
    }

    private async Task<byte[]> RunGitForOutputAsync(string repositoryPath, params string[] arguments)
    {
        var result = await RunGitForResultAsync(repositoryPath, arguments);
        Assert.AreEqual(0, result.ExitCode, Encoding.UTF8.GetString(result.StandardError.Span));
        return TrimLineEnding(result.StandardOutput.Span).ToArray();
    }

    private Task<ProcessResult> RunGitForResultAsync(string repositoryPath, params string[] arguments)
    {
        var environment = ChildEnvironment.Create(
        [
            new KeyValuePair<string, string>("HOME", _temporaryDirectory!),
            new KeyValuePair<string, string>("USERPROFILE", _temporaryDirectory!),
            new KeyValuePair<string, string>("GIT_CONFIG_NOSYSTEM", "1"),
            new KeyValuePair<string, string>("GIT_AUTHOR_NAME", "GitSail Test"),
            new KeyValuePair<string, string>("GIT_AUTHOR_EMAIL", "gitsail@example.invalid"),
            new KeyValuePair<string, string>("GIT_COMMITTER_NAME", "GitSail Test"),
            new KeyValuePair<string, string>("GIT_COMMITTER_EMAIL", "gitsail@example.invalid"),
            new KeyValuePair<string, string>("LANG", "C"),
            new KeyValuePair<string, string>("LC_ALL", "C"),
        ]);
        var invocation = new ProcessInvocation(
            _installation!.Executable,
            [.. arguments.Select(ProcessArgument.Literal)],
            CanonicalDirectory.Create(repositoryPath),
            environment,
            StandardInputSource.Empty(),
            OutputPolicy.Create(1024 * 1024, 1024 * 1024));
        return _runner!.RunAsync(invocation, TestContext.Current!.CancellationToken);
    }

    private TestProcessEnvironment CreateProcessEnvironment()
        => new(new Dictionary<string, string?>
        {
            ["HOME"] = _temporaryDirectory,
            ["USERPROFILE"] = _temporaryDirectory,
            ["XDG_CONFIG_HOME"] = Path.Combine(_temporaryDirectory!, "xdg-config"),
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_AUTHOR_NAME"] = "GitSail Test",
            ["GIT_AUTHOR_EMAIL"] = "gitsail@example.invalid",
            ["GIT_COMMITTER_NAME"] = "GitSail Test",
            ["GIT_COMMITTER_EMAIL"] = "gitsail@example.invalid",
            ["GIT_EDITOR"] = ":",
            ["PATH"] = Environment.GetEnvironmentVariable("PATH"),
            ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot"),
            ["WINDIR"] = Environment.GetEnvironmentVariable("WINDIR"),
        });

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

        Assert.Fail($"Text '{text}' was not found in the terminal snapshot.");
        return (-1, -1);
    }

    private static ReadOnlySpan<byte> TrimLineEnding(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty && bytes[^1] is (byte)'\n' or (byte)'\r')
        {
            bytes = bytes[..^1];
        }

        return bytes;
    }
}
