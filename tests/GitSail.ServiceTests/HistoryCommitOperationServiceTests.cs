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
/// Verifies cherry-pick, commit-revert, and stopped-operation recovery against real repositories.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class HistoryCommitOperationServiceTests
{
    private string? _temporaryDirectory;
    private GitInstallation? _installation;
    private ChildProcessRunner? _runner;
    private RepositoryMutationCoordinator? _coordinator;
    private GitChildEnvironmentFactory? _environmentFactory;
    private HistoryCommitOperationService? _service;

    /// <summary>
    /// Creates an isolated home and resolves Git for each history-operation test.
    /// </summary>
    [TestInitialize]
    public async Task InitializeAsync()
    {
        _temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"gitsail-history-operation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);
        _runner = new ChildProcessRunner();
        _coordinator = new RepositoryMutationCoordinator();
        _environmentFactory = TestProcessEnvironment.CreateGitFactory(_temporaryDirectory);
        _installation = await new GitVersionService(
            new ExecutableResolver(new RuntimeProcessEnvironment()),
            _runner).GetAsync(
                CanonicalDirectory.Create(_temporaryDirectory),
                TestContext.Current!.CancellationToken);
        _service = new HistoryCommitOperationService(
            _installation,
            _runner,
            _environmentFactory,
            _coordinator);
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
    /// Verifies cherry-pick applies the exact confirmed commit and creates a new current commit.
    /// </summary>
    [TestMethod]
    public async Task ExecuteAsync_WithCherryPick_AppliesExactCommit()
    {
        var repositoryPath = await CreateRepositoryAsync("cherry-pick");
        await RunGitAsync(repositoryPath, "switch", "--quiet", "--create", "feature");
        await File.WriteAllTextAsync(
            Path.Combine(repositoryPath, "feature.txt"),
            "feature\n",
            TestContext.Current!.CancellationToken);
        await RunGitAsync(repositoryPath, "add", "--", "feature.txt");
        await CommitAsync(repositoryPath, "feature commit");
        var selected = await ReadCommitAsync(repositoryPath, "HEAD");
        await RunGitAsync(repositoryPath, "switch", "--quiet", "main");
        var previousHead = await ReadObjectIdAsync(repositoryPath, "HEAD");
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var plan = await _service!.PrepareAsync(
            workingDirectory,
            selected,
            HistoryCommitOperation.CherryPick,
            mainlineParent: null,
            TestContext.Current!.CancellationToken);

        var result = await _service.ExecuteAsync(
            workingDirectory,
            plan,
            TestContext.Current.CancellationToken);

        Assert.AreEqual(HistoryCommitOperationOutcome.Completed, result.Outcome);
        Assert.IsNull(result.State);
        Assert.AreNotEqual(previousHead, await ReadObjectIdAsync(repositoryPath, "HEAD"));
        Assert.AreEqual("feature\n", File.ReadAllText(Path.Combine(repositoryPath, "feature.txt")));
        Assert.AreEqual("feature commit", await ReadTextAsync(repositoryPath, "log", "-1", "--format=%s"));
    }

    /// <summary>
    /// Verifies commit revert applies the inverse of the exact confirmed commit.
    /// </summary>
    [TestMethod]
    public async Task ExecuteAsync_WithCommitRevert_AppliesInverseCommit()
    {
        var repositoryPath = await CreateRepositoryAsync("revert");
        await File.WriteAllTextAsync(
            Path.Combine(repositoryPath, "remove-me.txt"),
            "content\n",
            TestContext.Current!.CancellationToken);
        await RunGitAsync(repositoryPath, "add", "--", "remove-me.txt");
        await CommitAsync(repositoryPath, "add removable file");
        var selected = await ReadCommitAsync(repositoryPath, "HEAD");
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var plan = await _service!.PrepareAsync(
            workingDirectory,
            selected,
            HistoryCommitOperation.Revert,
            mainlineParent: null,
            TestContext.Current.CancellationToken);

        var result = await _service.ExecuteAsync(
            workingDirectory,
            plan,
            TestContext.Current.CancellationToken);

        Assert.AreEqual(HistoryCommitOperationOutcome.Completed, result.Outcome);
        Assert.IsNull(result.State);
        Assert.IsFalse(File.Exists(Path.Combine(repositoryPath, "remove-me.txt")));
        Assert.AreEqual(
            "Revert \"add removable file\"",
            await ReadTextAsync(repositoryPath, "log", "-1", "--format=%s"));
    }

    /// <summary>
    /// Verifies a conflicting cherry-pick exposes exact stopped state and abort restores the branch.
    /// </summary>
    [TestMethod]
    public async Task ExecuteAsync_WithConflict_StopsAndAbortRestoresRepository()
    {
        var setup = await CreateConflictingCherryPickAsync("conflict-abort");

        var result = await _service!.ExecuteAsync(
            setup.WorkingDirectory,
            setup.Plan,
            TestContext.Current!.CancellationToken);

        Assert.AreEqual(HistoryCommitOperationOutcome.Stopped, result.Outcome);
        Assert.IsNotNull(result.State);
        Assert.AreEqual(HistoryCommitOperation.CherryPick, result.State.Operation);
        Assert.AreEqual(setup.Selected.ObjectId, result.State.Commit);
        var abort = await _service.AbortAsync(
            setup.WorkingDirectory,
            result.State,
            TestContext.Current.CancellationToken);
        Assert.AreEqual(HistoryCommitOperationOutcome.Completed, abort.Outcome);
        Assert.IsNull(await _service.CaptureStateAsync(
            setup.WorkingDirectory,
            TestContext.Current.CancellationToken));
        Assert.AreEqual(setup.PreviousHead, await ReadObjectIdAsync(setup.RepositoryPath, "HEAD"));
        Assert.AreEqual("main\n", File.ReadAllText(Path.Combine(setup.RepositoryPath, "tracked.txt")));
    }

    /// <summary>
    /// Verifies resolving and continuing a stopped cherry-pick completes the selected commit.
    /// </summary>
    [TestMethod]
    public async Task ContinueAsync_AfterConflictResolution_CompletesCherryPick()
    {
        var setup = await CreateConflictingCherryPickAsync("conflict-continue");
        var stopped = await _service!.ExecuteAsync(
            setup.WorkingDirectory,
            setup.Plan,
            TestContext.Current!.CancellationToken);
        Assert.IsNotNull(stopped.State);
        await File.WriteAllTextAsync(
            Path.Combine(setup.RepositoryPath, "tracked.txt"),
            "resolved\n",
            TestContext.Current.CancellationToken);
        await RunGitAsync(setup.RepositoryPath, "add", "--", "tracked.txt");

        var result = await _service.ContinueAsync(
            setup.WorkingDirectory,
            stopped.State,
            TestContext.Current.CancellationToken);

        Assert.AreEqual(HistoryCommitOperationOutcome.Completed, result.Outcome);
        Assert.IsNull(result.State);
        Assert.AreNotEqual(setup.PreviousHead, await ReadObjectIdAsync(setup.RepositoryPath, "HEAD"));
        Assert.AreEqual("resolved\n", File.ReadAllText(Path.Combine(setup.RepositoryPath, "tracked.txt")));
        Assert.AreEqual("feature edit", await ReadTextAsync(
            setup.RepositoryPath,
            "log",
            "-1",
            "--format=%s"));
    }

    /// <summary>
    /// Verifies skipping a stopped cherry-pick clears partial application without moving HEAD.
    /// </summary>
    [TestMethod]
    public async Task SkipAsync_WithStoppedCherryPick_ClearsOperationWithoutCommit()
    {
        var setup = await CreateConflictingCherryPickAsync("conflict-skip");
        var stopped = await _service!.ExecuteAsync(
            setup.WorkingDirectory,
            setup.Plan,
            TestContext.Current!.CancellationToken);
        Assert.IsNotNull(stopped.State);

        var result = await _service.SkipAsync(
            setup.WorkingDirectory,
            stopped.State,
            TestContext.Current.CancellationToken);

        Assert.AreEqual(HistoryCommitOperationOutcome.Completed, result.Outcome);
        Assert.IsNull(result.State);
        Assert.AreEqual(setup.PreviousHead, await ReadObjectIdAsync(setup.RepositoryPath, "HEAD"));
        Assert.AreEqual("main\n", File.ReadAllText(Path.Combine(setup.RepositoryPath, "tracked.txt")));
    }

    /// <summary>
    /// Verifies worktree changes after confirmation prevent the exact operation from starting.
    /// </summary>
    [TestMethod]
    public async Task ExecuteAsync_AfterWorktreeChange_RejectsStalePlan()
    {
        var repositoryPath = await CreateRepositoryAsync("stale-worktree");
        var selected = await ReadCommitAsync(repositoryPath, "HEAD");
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var plan = await _service!.PrepareAsync(
            workingDirectory,
            selected,
            HistoryCommitOperation.Revert,
            mainlineParent: null,
            TestContext.Current!.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(repositoryPath, "untracked.txt"),
            "changed after confirmation\n",
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsExactlyAsync<RepositoryPreconditionException>(
            () => _service.ExecuteAsync(
                workingDirectory,
                plan,
                TestContext.Current.CancellationToken));

        StringAssert.Contains(exception.Message, "worktree changed", StringComparison.Ordinal);
        Assert.IsNull(await _service.CaptureStateAsync(
            workingDirectory,
            TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies a merge commit requires and honors the user's exact mainline parent selection.
    /// </summary>
    [TestMethod]
    public async Task ExecuteAsync_WithMergeCommit_AppliesSelectedMainlineDifference()
    {
        var repositoryPath = await CreateRepositoryAsync("merge-mainline");
        var baseObjectId = await ReadObjectIdAsync(repositoryPath, "HEAD");
        await RunGitAsync(repositoryPath, "switch", "--quiet", "--create", "side");
        await File.WriteAllTextAsync(
            Path.Combine(repositoryPath, "side.txt"),
            "side\n",
            TestContext.Current!.CancellationToken);
        await RunGitAsync(repositoryPath, "add", "--", "side.txt");
        await CommitAsync(repositoryPath, "side change");
        await RunGitAsync(repositoryPath, "switch", "--quiet", "main");
        await File.WriteAllTextAsync(
            Path.Combine(repositoryPath, "main.txt"),
            "main\n",
            TestContext.Current.CancellationToken);
        await RunGitAsync(repositoryPath, "add", "--", "main.txt");
        await CommitAsync(repositoryPath, "main change");
        await RunGitAsync(
            repositoryPath,
            "merge",
            "--quiet",
            "--no-ff",
            "--no-gpg-sign",
            "--message=merge side",
            "side");
        var selected = await ReadCommitAsync(repositoryPath, "HEAD");
        Assert.HasCount(2, selected.Parents);
        await RunGitAsync(repositoryPath, "switch", "--quiet", "--detach", baseObjectId.ToString());
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);

        _ = await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            () => _service!.PrepareAsync(
                workingDirectory,
                selected,
                HistoryCommitOperation.CherryPick,
                mainlineParent: null,
                TestContext.Current.CancellationToken));
        var plan = await _service!.PrepareAsync(
            workingDirectory,
            selected,
            HistoryCommitOperation.CherryPick,
            mainlineParent: 1,
            TestContext.Current.CancellationToken);
        var result = await _service.ExecuteAsync(
            workingDirectory,
            plan,
            TestContext.Current.CancellationToken);

        Assert.AreEqual(HistoryCommitOperationOutcome.Completed, result.Outcome);
        Assert.AreEqual(1, plan.MainlineParent);
        Assert.AreEqual("side\n", File.ReadAllText(Path.Combine(repositoryPath, "side.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(repositoryPath, "main.txt")));
    }

    /// <summary>
    /// Verifies a conflicting commit revert is classified with exact revert state and can abort.
    /// </summary>
    [TestMethod]
    public async Task ExecuteAsync_WithRevertConflict_ReportsRevertState()
    {
        var repositoryPath = await CreateRepositoryAsync("revert-conflict");
        await File.WriteAllTextAsync(
            Path.Combine(repositoryPath, "tracked.txt"),
            "selected\n",
            TestContext.Current!.CancellationToken);
        await RunGitAsync(repositoryPath, "add", "--", "tracked.txt");
        await CommitAsync(repositoryPath, "selected edit");
        var selected = await ReadCommitAsync(repositoryPath, "HEAD");
        await File.WriteAllTextAsync(
            Path.Combine(repositoryPath, "tracked.txt"),
            "current\n",
            TestContext.Current.CancellationToken);
        await RunGitAsync(repositoryPath, "add", "--", "tracked.txt");
        await CommitAsync(repositoryPath, "current edit");
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var plan = await _service!.PrepareAsync(
            workingDirectory,
            selected,
            HistoryCommitOperation.Revert,
            mainlineParent: null,
            TestContext.Current.CancellationToken);

        var result = await _service.ExecuteAsync(
            workingDirectory,
            plan,
            TestContext.Current.CancellationToken);

        Assert.AreEqual(HistoryCommitOperationOutcome.Stopped, result.Outcome);
        Assert.IsNotNull(result.State);
        Assert.AreEqual(HistoryCommitOperation.Revert, result.State.Operation);
        Assert.AreEqual(selected.ObjectId, result.State.Commit);
        var abort = await _service.AbortAsync(
            workingDirectory,
            result.State,
            TestContext.Current.CancellationToken);
        Assert.AreEqual(HistoryCommitOperationOutcome.Completed, abort.Outcome);
        Assert.AreEqual("current\n", File.ReadAllText(Path.Combine(repositoryPath, "tracked.txt")));
    }

    /// <summary>
    /// Verifies an invalid committer identity is rejected before Git can start repository state.
    /// </summary>
    [TestMethod]
    public async Task PrepareAsync_WithInvalidCommitterIdentity_LeavesRepositoryUntouched()
    {
        var repositoryPath = await CreateRepositoryAsync("missing-identity");
        var selected = await ReadCommitAsync(repositoryPath, "HEAD");
        var previousHead = await ReadObjectIdAsync(repositoryPath, "HEAD");
        var environmentFactory = new GitChildEnvironmentFactory(new TestProcessEnvironment(
            new Dictionary<string, string?>
            {
                ["HOME"] = _temporaryDirectory,
                ["USERPROFILE"] = _temporaryDirectory,
                ["XDG_CONFIG_HOME"] = Path.Combine(_temporaryDirectory!, "invalid-identity-config"),
                ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["GIT_AUTHOR_NAME"] = string.Empty,
                ["GIT_AUTHOR_EMAIL"] = string.Empty,
                ["GIT_COMMITTER_NAME"] = string.Empty,
                ["GIT_COMMITTER_EMAIL"] = string.Empty,
            }));
        var service = new HistoryCommitOperationService(
            _installation!,
            _runner!,
            environmentFactory,
            _coordinator!);
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);

        var exception = await Assert.ThrowsExactlyAsync<GitCommandException>(
            () => service.PrepareAsync(
                workingDirectory,
                selected,
                HistoryCommitOperation.Revert,
                mainlineParent: null,
                TestContext.Current!.CancellationToken));

        StringAssert.Contains(exception.Message, "ident", StringComparison.OrdinalIgnoreCase);
        Assert.IsNull(await service.CaptureStateAsync(
            workingDirectory,
            TestContext.Current!.CancellationToken));
        Assert.AreEqual(previousHead, await ReadObjectIdAsync(repositoryPath, "HEAD"));
        Assert.AreEqual("base\n", File.ReadAllText(Path.Combine(repositoryPath, "tracked.txt")));
    }

    /// <summary>
    /// Verifies history renders stopped controls and pointer-confirmed abort clears Git state.
    /// </summary>
    [TestMethod]
    public async Task HistoryView_WithStoppedCherryPick_ShowsRecoveryActions()
    {
        var setup = await CreateConflictingCherryPickAsync("stopped-view");
        var stopped = await _service!.ExecuteAsync(
            setup.WorkingDirectory,
            setup.Plan,
            TestContext.Current!.CancellationToken);
        Assert.AreEqual(HistoryCommitOperationOutcome.Stopped, stopped.Outcome);
        using var session = await HistorySession.OpenAsync(
            setup.WorkingDirectory,
            new HistoryOptions(RevisionRange: null, Pathspecs: []),
            CreateProcessEnvironment(),
            TestContext.Current.CancellationToken);
        await session.LoadAsync(TestContext.Current.CancellationToken);
        Assert.IsNotNull(session.PendingOperation);
        var view = new HistoryView(session, TestContext.Current.CancellationToken);
        Hex1bApp? application = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
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
            await automator.WaitUntilTextAsync("C Continue", TimeSpan.FromSeconds(5));
            using (var stoppedView = automator.CreateSnapshot())
            {
                Assert.IsTrue(stoppedView.ContainsText("S Skip"));
                Assert.IsTrue(stoppedView.ContainsText("A Abort"));
                Assert.IsTrue(stoppedView.ContainsText("Continue"));
                Assert.IsTrue(stoppedView.ContainsText("Skip..."));
                var abortAction = FindText(stoppedView, "Abort...");
                await automator.ClickAtAsync(
                    abortAction.X + 1,
                    abortAction.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilTextAsync("Abort cherry-pick?", TimeSpan.FromSeconds(5));
            using (var confirmation = automator.CreateSnapshot())
            {
                Assert.IsTrue(confirmation.ContainsText($"Commit: {setup.Selected.ObjectId}"));
                var approval = FindTextOnLineWith(confirmation, "Abort", "Cancel");
                await automator.ClickAtAsync(
                    approval.X + 1,
                    approval.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilAsync(
                snapshot => session.PendingOperation is null &&
                    snapshot.ContainsText("Cherry-pick...") &&
                    !snapshot.ContainsText("Abort cherry-pick?"),
                TimeSpan.FromSeconds(10),
                "The pointer-confirmed abort clears stopped state and restores history actions");
            Assert.AreEqual("main\n", File.ReadAllText(
                Path.Combine(setup.RepositoryPath, "tracked.txt")));
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    private async Task<string> CreateRepositoryAsync(string name)
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

    private async Task<(
        string RepositoryPath,
        CanonicalDirectory WorkingDirectory,
        HistoryCommitOperationPlan Plan,
        HistoryCommit Selected,
        ObjectId PreviousHead)> CreateConflictingCherryPickAsync(string name)
    {
        var repositoryPath = await CreateRepositoryAsync(name);
        await RunGitAsync(repositoryPath, "switch", "--quiet", "--create", "feature");
        await File.WriteAllTextAsync(
            Path.Combine(repositoryPath, "tracked.txt"),
            "feature\n",
            TestContext.Current!.CancellationToken);
        await RunGitAsync(repositoryPath, "add", "--", "tracked.txt");
        await CommitAsync(repositoryPath, "feature edit");
        var selected = await ReadCommitAsync(repositoryPath, "HEAD");
        await RunGitAsync(repositoryPath, "switch", "--quiet", "main");
        await File.WriteAllTextAsync(
            Path.Combine(repositoryPath, "tracked.txt"),
            "main\n",
            TestContext.Current.CancellationToken);
        await RunGitAsync(repositoryPath, "add", "--", "tracked.txt");
        await CommitAsync(repositoryPath, "main edit");
        var previousHead = await ReadObjectIdAsync(repositoryPath, "HEAD");
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var plan = await _service!.PrepareAsync(
            workingDirectory,
            selected,
            HistoryCommitOperation.CherryPick,
            mainlineParent: null,
            TestContext.Current.CancellationToken);
        return (repositoryPath, workingDirectory, plan, selected, previousHead);
    }

    private Task CommitAsync(string repositoryPath, string message)
        => RunGitAsync(
            repositoryPath,
            "commit",
            "--quiet",
            "--no-gpg-sign",
            $"--message={message}");

    private async Task<HistoryCommit> ReadCommitAsync(string repositoryPath, string revision)
    {
        var service = new HistoryService(_installation!, _runner!, _environmentFactory!);
        var catalog = await service.CaptureAsync(
            CanonicalDirectory.Create(repositoryPath),
            new HistoryQuery(
                Revision.Create(revision),
                Pathspecs: [],
                MaximumCommitCount: 1),
            TestContext.Current!.CancellationToken);
        var commit = catalog.Commits.SingleOrDefault();
        Assert.IsNotNull(commit);
        return commit;
    }

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
            ["PATH"] = Environment.GetEnvironmentVariable("PATH"),
            ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot"),
            ["WINDIR"] = Environment.GetEnvironmentVariable("WINDIR"),
        });

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

    private static (int X, int Y) FindTextOnLineWith(
        Hex1bTerminalSnapshot snapshot,
        string text,
        string companion)
    {
        for (var row = 0; row < snapshot.Height; row++)
        {
            var line = snapshot.GetLine(row);
            var column = line.IndexOf(text, StringComparison.Ordinal);
            if (column >= 0 && line.Contains(companion, StringComparison.Ordinal))
            {
                return (column, row);
            }
        }

        Assert.Fail($"Text '{text}' was not found on a line with '{companion}'.");
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
