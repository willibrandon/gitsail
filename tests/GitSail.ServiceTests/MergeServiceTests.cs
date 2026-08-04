using GitSail.Domain;
using GitSail.Git.Execution;
using System.Text;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies exact merge planning and Git-owned outcomes against real repositories.
/// </summary>
[TestClass]
public sealed class MergeServiceTests
{
    private string? _temporaryDirectory;
    private GitInstallation? _installation;
    private ChildProcessRunner? _runner;
    private RepositoryMutationCoordinator? _coordinator;
    private GitChildEnvironmentFactory? _environmentFactory;

    /// <summary>
    /// Creates an isolated home and resolves Git for each merge-service test.
    /// </summary>
    [TestInitialize]
    public async Task InitializeAsync()
    {
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"gitsail-merges-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);
        _runner = new ChildProcessRunner();
        _coordinator = new RepositoryMutationCoordinator();
        _environmentFactory = TestProcessEnvironment.CreateGitFactory(_temporaryDirectory);
        var resolver = new ExecutableResolver(new RuntimeProcessEnvironment());
        _installation = await new GitVersionService(resolver, _runner).GetAsync(
            CanonicalDirectory.Create(_temporaryDirectory),
            TestContext.Current!.CancellationToken);
    }

    /// <summary>
    /// Removes isolated repositories and the mutation coordinator after each test.
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
    /// Verifies an exact fast-forward plan moves HEAD to the displayed incoming object.
    /// </summary>
    [TestMethod]
    public async Task ExecuteAsync_WithFastForwardPlan_MovesToExactIncomingObject()
    {
        var repositoryPath = await CreateRepositoryAsync("fast-forward");
        await RunGitAsync(repositoryPath, "switch", "--quiet", "--create", "feature");
        File.AppendAllText(Path.Combine(repositoryPath, "tracked.txt"), "feature\n");
        await RunGitAsync(repositoryPath, "add", "--", "tracked.txt");
        await CommitAsync(repositoryPath, "feature");
        var incomingObjectId = await ReadObjectIdAsync(repositoryPath, "HEAD");
        await RunGitAsync(repositoryPath, "switch", "--quiet", "main");
        var service = CreateService();
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var catalog = await CaptureBranchesAsync(workingDirectory);
        var plan = await service.PrepareAsync(
            workingDirectory,
            catalog,
            FindBranch(catalog, "refs/heads/feature"),
            TestContext.Current!.CancellationToken);

        Assert.AreEqual(MergeRelationship.FastForward, plan.Relationship);
        Assert.AreEqual(0, plan.CurrentOnlyCommitCount);
        Assert.AreEqual(1, plan.IncomingCommitCount);
        var result = await service.ExecuteAsync(
            workingDirectory,
            plan,
            MergeOptions.CreateDefault(),
            TestContext.Current.CancellationToken);

        Assert.AreEqual(MergeOutcome.Completed, result.Outcome);
        Assert.IsFalse(result.HasMergeHead);
        Assert.AreEqual(incomingObjectId, await ReadObjectIdAsync(repositoryPath, "HEAD"));
        Assert.AreEqual("baseline\nfeature\n", File.ReadAllText(Path.Combine(repositoryPath, "tracked.txt")));
    }

    /// <summary>
    /// Verifies a divergent no-commit merge leaves Git-owned pending merge state for review.
    /// </summary>
    [TestMethod]
    public async Task ExecuteAsync_WithNoCommitDivergence_LeavesPendingMergeForWorkspaceCommit()
    {
        var repositoryPath = await CreateDivergedRepositoryAsync("no-commit", conflicting: false);
        var service = CreateService();
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var catalog = await CaptureBranchesAsync(workingDirectory);
        var plan = await service.PrepareAsync(
            workingDirectory,
            catalog,
            FindBranch(catalog, "refs/heads/feature"),
            TestContext.Current!.CancellationToken);
        var options = new MergeOptions(
            MergeFastForwardMode.NoFastForward,
            MergeStrategy.Ort,
            MergeConflictPreference.Default,
            squash: false,
            stopBeforeCommit: true,
            GitOptionOverride.Disabled,
            GitOptionOverride.Configured,
            GitOptionOverride.Configured);

        Assert.AreEqual(MergeRelationship.Diverged, plan.Relationship);
        Assert.AreEqual(1, plan.CurrentOnlyCommitCount);
        Assert.AreEqual(1, plan.IncomingCommitCount);
        var result = await service.ExecuteAsync(
            workingDirectory,
            plan,
            options,
            TestContext.Current.CancellationToken);

        Assert.AreEqual(MergeOutcome.StoppedBeforeCommit, result.Outcome);
        Assert.IsTrue(result.HasMergeHead);
        _ = await ReadObjectIdAsync(repositoryPath, "MERGE_HEAD");
        Assert.AreEqual("main\n", File.ReadAllText(Path.Combine(repositoryPath, "main.txt")));
        Assert.AreEqual("feature\n", File.ReadAllText(Path.Combine(repositoryPath, "feature.txt")));
    }

    /// <summary>
    /// Verifies content conflicts are a classified workspace transition rather than false success.
    /// </summary>
    [TestMethod]
    public async Task ExecuteAsync_WithContentConflict_ReturnsConflictOutcomeAndUnmergedIndex()
    {
        var repositoryPath = await CreateDivergedRepositoryAsync("conflict", conflicting: true);
        var service = CreateService();
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var catalog = await CaptureBranchesAsync(workingDirectory);
        var plan = await service.PrepareAsync(
            workingDirectory,
            catalog,
            FindBranch(catalog, "refs/heads/feature"),
            TestContext.Current!.CancellationToken);

        var result = await service.ExecuteAsync(
            workingDirectory,
            plan,
            MergeOptions.CreateDefault(),
            TestContext.Current.CancellationToken);

        Assert.AreEqual(MergeOutcome.Conflicts, result.Outcome);
        Assert.IsTrue(result.HasMergeHead);
        Assert.IsGreaterThan(0, (await RunGitForOutputAsync(repositoryPath, "ls-files", "--unmerged")).Length);
        Assert.IsGreaterThan(0, result.Operation.StandardError.Length + result.Operation.StandardOutput.Length);
    }

    /// <summary>
    /// Verifies typed ort-theirs and explicit negative overrides reach Git without free-form options.
    /// </summary>
    [TestMethod]
    public async Task ExecuteAsync_WithAllowlistedTheirsPreference_ResolvesOnlyConflictingContent()
    {
        var repositoryPath = await CreateDivergedRepositoryAsync("theirs-preference", conflicting: true);
        var service = CreateService();
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var catalog = await CaptureBranchesAsync(workingDirectory);
        var plan = await service.PrepareAsync(
            workingDirectory,
            catalog,
            FindBranch(catalog, "refs/heads/feature"),
            TestContext.Current!.CancellationToken);
        var options = new MergeOptions(
            MergeFastForwardMode.NoFastForward,
            MergeStrategy.Ort,
            MergeConflictPreference.Theirs,
            squash: false,
            stopBeforeCommit: false,
            GitOptionOverride.Disabled,
            GitOptionOverride.Disabled,
            GitOptionOverride.Disabled);

        var result = await service.ExecuteAsync(
            workingDirectory,
            plan,
            options,
            TestContext.Current.CancellationToken);

        Assert.AreEqual(MergeOutcome.Completed, result.Outcome);
        Assert.AreEqual("feature\n", File.ReadAllText(Path.Combine(repositoryPath, "tracked.txt")));
        Assert.HasCount(
            3,
            (await RunGitForOutputAsync(repositoryPath, "rev-list", "--parents", "--max-count=1", "HEAD"))
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Verifies a changed tracked worktree invalidates the exact displayed merge confirmation.
    /// </summary>
    [TestMethod]
    public async Task ExecuteAsync_AfterWorktreeChanged_RejectsStaleMergeConfirmation()
    {
        var repositoryPath = await CreateRepositoryAsync("stale-worktree");
        await RunGitAsync(repositoryPath, "branch", "feature");
        var service = CreateService();
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var catalog = await CaptureBranchesAsync(workingDirectory);
        var plan = await service.PrepareAsync(
            workingDirectory,
            catalog,
            FindBranch(catalog, "refs/heads/feature"),
            TestContext.Current!.CancellationToken);
        File.AppendAllText(Path.Combine(repositoryPath, "tracked.txt"), "concurrent\n");

        _ = await Assert.ThrowsExactlyAsync<RepositoryPreconditionException>(() => service.ExecuteAsync(
            workingDirectory,
            plan,
            MergeOptions.CreateDefault(),
            TestContext.Current.CancellationToken));

        Assert.AreEqual("baseline\nconcurrent\n", File.ReadAllText(Path.Combine(repositoryPath, "tracked.txt")));
    }

    private MergeService CreateService()
        => new(_installation!, _runner!, _environmentFactory!, _coordinator!);

    private Task<BranchCatalog> CaptureBranchesAsync(CanonicalDirectory workingDirectory)
        => new BranchService(_installation!, _runner!, _environmentFactory!, _coordinator!).CaptureAsync(
            workingDirectory,
            TestContext.Current!.CancellationToken);

    private async Task<string> CreateDivergedRepositoryAsync(string directoryName, bool conflicting)
    {
        var repositoryPath = await CreateRepositoryAsync(directoryName);
        await RunGitAsync(repositoryPath, "switch", "--quiet", "--create", "feature");
        if (conflicting)
        {
            File.WriteAllText(Path.Combine(repositoryPath, "tracked.txt"), "feature\n");
            await RunGitAsync(repositoryPath, "add", "--", "tracked.txt");
        }
        else
        {
            File.WriteAllText(Path.Combine(repositoryPath, "feature.txt"), "feature\n");
            await RunGitAsync(repositoryPath, "add", "--", "feature.txt");
        }

        await CommitAsync(repositoryPath, "feature");
        await RunGitAsync(repositoryPath, "switch", "--quiet", "main");
        if (conflicting)
        {
            File.WriteAllText(Path.Combine(repositoryPath, "tracked.txt"), "main\n");
            await RunGitAsync(repositoryPath, "add", "--", "tracked.txt");
        }
        else
        {
            File.WriteAllText(Path.Combine(repositoryPath, "main.txt"), "main\n");
            await RunGitAsync(repositoryPath, "add", "--", "main.txt");
        }

        await CommitAsync(repositoryPath, "main");
        return repositoryPath;
    }

    private async Task<string> CreateRepositoryAsync(string directoryName)
    {
        var repositoryPath = Path.Combine(_temporaryDirectory!, directoryName);
        await RunGitAsync(
            _temporaryDirectory!,
            "init",
            "--quiet",
            "--initial-branch=main",
            "--",
            repositoryPath);
        await RunGitAsync(repositoryPath, "config", "user.name", "GitSail Tests");
        await RunGitAsync(repositoryPath, "config", "user.email", "gitsail@example.invalid");
        File.WriteAllText(Path.Combine(repositoryPath, "tracked.txt"), "baseline\n");
        await RunGitAsync(repositoryPath, "add", "--", "tracked.txt");
        await CommitAsync(repositoryPath, "baseline");
        return repositoryPath;
    }

    private Task CommitAsync(string repositoryPath, string message)
        => RunGitAsync(repositoryPath, "commit", "--quiet", "-m", message);

    private async Task<ObjectId> ReadObjectIdAsync(string repositoryPath, string revision)
    {
        var bytes = Encoding.ASCII.GetBytes((await RunGitForOutputAsync(
            repositoryPath,
            "rev-parse",
            "--verify",
            revision)).Trim());
        Assert.IsTrue(ObjectId.TryParseHex(bytes, out var objectId));
        return objectId!;
    }

    private async Task RunGitAsync(string workingDirectory, params string[] arguments)
        => _ = await RunGitForOutputAsync(workingDirectory, arguments);

    private async Task<string> RunGitForOutputAsync(string workingDirectory, params string[] arguments)
    {
        var environment = ChildEnvironment.Create(
        [
            new KeyValuePair<string, string>("HOME", _temporaryDirectory!),
            new KeyValuePair<string, string>("USERPROFILE", _temporaryDirectory!),
            new KeyValuePair<string, string>("GIT_CONFIG_NOSYSTEM", "1"),
            new KeyValuePair<string, string>("LANG", "C"),
            new KeyValuePair<string, string>("LC_ALL", "C"),
        ]);
        var invocation = new ProcessInvocation(
            _installation!.Executable,
            [.. arguments.Select(ProcessArgument.Literal)],
            CanonicalDirectory.Create(workingDirectory),
            environment,
            StandardInputSource.Empty(),
            OutputPolicy.Create(64 * 1024 * 1024, 4 * 1024 * 1024));
        var result = await _runner!.RunAsync(invocation, TestContext.Current!.CancellationToken);
        Assert.AreEqual(0, result.ExitCode, Encoding.UTF8.GetString(result.StandardError.Span));
        return Encoding.UTF8.GetString(result.StandardOutput.Span);
    }

    private static BranchInfo FindBranch(BranchCatalog catalog, string fullName)
    {
        var branch = catalog.Find(RefName.FromBytes(Encoding.UTF8.GetBytes(fullName)));
        Assert.IsNotNull(branch);
        return branch;
    }
}
