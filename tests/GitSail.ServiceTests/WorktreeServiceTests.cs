using GitSail.Domain;
using GitSail.Git.Execution;
using System.Text;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies linked-worktree creation and management against real Git repositories.
/// </summary>
[TestClass]
public sealed class WorktreeServiceTests
{
    private string? _temporaryDirectory;
    private string? _repositoryPath;
    private GitInstallation? _installation;
    private ChildProcessRunner? _runner;
    private TestProcessEnvironment? _processEnvironment;
    private BranchService? _branchService;
    private WorktreeService? _worktreeService;

    /// <summary>
    /// Creates an isolated repository, branch catalog service, and linked-worktree service.
    /// </summary>
    [TestInitialize]
    public async Task InitializeAsync()
    {
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"gitsail-worktree-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);
        _repositoryPath = Path.Combine(_temporaryDirectory, "main repository");
        Directory.CreateDirectory(_repositoryPath);
        _runner = new ChildProcessRunner();
        _processEnvironment = CreateProcessEnvironment();
        _installation = await new GitVersionService(
            new ExecutableResolver(_processEnvironment),
            _runner).GetAsync(
            CanonicalDirectory.Create(_temporaryDirectory),
            TestContext.Current!.CancellationToken);
        await RunGitAsync(_repositoryPath, "init", "--quiet", "--initial-branch=main");
        await File.WriteAllTextAsync(
            Path.Combine(_repositoryPath, "tracked.txt"),
            "main content\n",
            TestContext.Current.CancellationToken);
        await RunGitAsync(_repositoryPath, "add", "--", "tracked.txt");
        await CommitAsync(_repositoryPath, "initial");
        await RunGitAsync(_repositoryPath, "branch", "topic");
        var environmentFactory = new GitChildEnvironmentFactory(_processEnvironment);
        var coordinator = new RepositoryMutationCoordinator();
        _branchService = new BranchService(
            _installation,
            _runner,
            environmentFactory,
            coordinator);
        _worktreeService = new WorktreeService(
            _installation,
            _runner,
            environmentFactory,
            coordinator,
            _branchService);
    }

    /// <summary>
    /// Removes every isolated linked worktree and repository after each test.
    /// </summary>
    [TestCleanup]
    public void Cleanup()
    {
        if (_temporaryDirectory is not null && Directory.Exists(_temporaryDirectory))
        {
            TestDirectory.Delete(_temporaryDirectory);
        }
    }

    /// <summary>
    /// Verifies existing-branch, new-branch, and detached modes create the requested exact HEAD state.
    /// </summary>
    /// <param name="modeValue">The linked-worktree HEAD mode.</param>
    [TestMethod]
    [DataRow((int)WorktreeAddMode.ExistingBranch)]
    [DataRow((int)WorktreeAddMode.NewBranch)]
    [DataRow((int)WorktreeAddMode.Detached)]
    public async Task AddAsync_WithEveryHeadMode_CreatesExpectedWorktree(int modeValue)
    {
        var mode = (WorktreeAddMode)modeValue;
        var workingDirectory = CanonicalDirectory.Create(_repositoryPath!);
        var catalog = await CaptureAsync();
        var source = catalog.Find(RefName.FromBytes(
            mode == WorktreeAddMode.ExistingBranch ? "refs/heads/topic"u8 : "refs/heads/main"u8));
        Assert.IsNotNull(source);
        var targetPath = Path.Combine(_temporaryDirectory!, $"created {mode}");
        var newName = mode == WorktreeAddMode.NewBranch
            ? await _branchService!.ValidateLocalNameAsync(
                workingDirectory,
                "worktree-created",
                TestContext.Current!.CancellationToken)
            : null;

        var result = await _worktreeService!.AddAsync(
            workingDirectory,
            catalog,
            new WorktreeAddRequest(
                targetPath,
                source!,
                mode,
                newName,
                TrackStartingPoint: false,
                LockAfterCreation: false,
                LockReason: null),
            TestContext.Current!.CancellationToken);

        Assert.AreEqual(GetManagedPath(CanonicalDirectory.Create(targetPath)), GetManagedPath(result.Directory));
        Assert.AreEqual("main content\n", await File.ReadAllTextAsync(
            Path.Combine(targetPath, "tracked.txt"),
            TestContext.Current.CancellationToken));
        var branch = (await RunGitForOutputAsync(
            targetPath,
            "symbolic-ref",
            "--quiet",
            "HEAD")).Trim();
        Assert.AreEqual(mode switch
        {
            WorktreeAddMode.ExistingBranch => "refs/heads/topic",
            WorktreeAddMode.NewBranch => "refs/heads/worktree-created",
            WorktreeAddMode.Detached => string.Empty,
            _ => throw new ArgumentOutOfRangeException(nameof(modeValue)),
        }, branch);
    }

    /// <summary>
    /// Verifies locking retains an exact multiline reason and unlocking restores mutable state.
    /// </summary>
    [TestMethod]
    public async Task LockAndUnlockAsync_WithReason_RoundTripsThroughGitCatalog()
    {
        var targetPath = await AddTopicWorktreeAsync("lock target");
        var catalog = await CaptureAsync();
        var worktree = FindWorktree(catalog, targetPath);

        _ = await _worktreeService!.LockAsync(
            CanonicalDirectory.Create(_repositoryPath!),
            catalog,
            worktree,
            "portable\nvolume",
            TestContext.Current!.CancellationToken);

        catalog = await CaptureAsync();
        worktree = FindWorktree(catalog, targetPath);
        Assert.IsTrue(worktree.IsLocked);
        Assert.AreEqual("portable<0x0A>volume", worktree.LockReasonDisplay);
        _ = await _worktreeService.UnlockAsync(
            CanonicalDirectory.Create(_repositoryPath!),
            catalog,
            worktree,
            TestContext.Current.CancellationToken);
        Assert.IsFalse(FindWorktree(await CaptureAsync(), targetPath).IsLocked);
    }

    /// <summary>
    /// Verifies creation can atomically lock a detached worktree with its literal reason.
    /// </summary>
    [TestMethod]
    public async Task AddAsync_WithAtomicLock_CreatesLockedWorktree()
    {
        var catalog = await CaptureAsync();
        var source = catalog.Find(RefName.FromBytes("refs/heads/main"u8));
        Assert.IsNotNull(source);
        var targetPath = Path.Combine(_temporaryDirectory!, "atomically locked");

        _ = await _worktreeService!.AddAsync(
            CanonicalDirectory.Create(_repositoryPath!),
            catalog,
            new WorktreeAddRequest(
                targetPath,
                source!,
                WorktreeAddMode.Detached,
                NewBranchName: null,
                TrackStartingPoint: false,
                LockAfterCreation: true,
                LockReason: "portable volume"),
            TestContext.Current!.CancellationToken);

        var worktree = FindWorktree(await CaptureAsync(), targetPath);
        Assert.IsTrue(worktree.IsLocked);
        Assert.AreEqual("portable volume", worktree.LockReasonDisplay);
    }

    /// <summary>
    /// Verifies a new worktree branch can directly track an exact remote-tracking starting point.
    /// </summary>
    [TestMethod]
    public async Task AddAsync_WithRemoteTrackingStart_ConfiguresDirectUpstream()
    {
        await RunGitAsync(_repositoryPath!, "remote", "add", "origin", _repositoryPath!);
        var head = (await RunGitForOutputAsync(_repositoryPath!, "rev-parse", "HEAD")).Trim();
        await RunGitAsync(
            _repositoryPath!,
            "update-ref",
            "refs/remotes/origin/remote-topic",
            head);
        var catalog = await CaptureAsync();
        var source = catalog.Find(RefName.FromBytes("refs/remotes/origin/remote-topic"u8));
        Assert.IsNotNull(source);
        var name = await _branchService!.ValidateLocalNameAsync(
            CanonicalDirectory.Create(_repositoryPath!),
            "tracked-worktree",
            TestContext.Current!.CancellationToken);
        var targetPath = Path.Combine(_temporaryDirectory!, "tracked worktree");

        _ = await _worktreeService!.AddAsync(
            CanonicalDirectory.Create(_repositoryPath!),
            catalog,
            new WorktreeAddRequest(
                targetPath,
                source!,
                WorktreeAddMode.NewBranch,
                name,
                TrackStartingPoint: true,
                LockAfterCreation: false,
                LockReason: null),
            TestContext.Current!.CancellationToken);

        Assert.AreEqual("origin", (await RunGitForOutputAsync(
            targetPath,
            "config",
            "--get",
            "branch.tracked-worktree.remote")).Trim());
        Assert.AreEqual("refs/heads/remote-topic", (await RunGitForOutputAsync(
            targetPath,
            "config",
            "--get",
            "branch.tracked-worktree.merge")).Trim());
    }

    /// <summary>
    /// Verifies movement updates Git's exact worktree path and preserves checked-out content.
    /// </summary>
    [TestMethod]
    public async Task MoveAsync_WithUnoccupiedTarget_UpdatesCatalogAndFilesystem()
    {
        var originalPath = await AddTopicWorktreeAsync("move source");
        var destinationPath = Path.Combine(_temporaryDirectory!, "move destination");
        var catalog = await CaptureAsync();

        var result = await _worktreeService!.MoveAsync(
            CanonicalDirectory.Create(_repositoryPath!),
            catalog,
            FindWorktree(catalog, originalPath),
            destinationPath,
            TestContext.Current!.CancellationToken);

        Assert.IsFalse(Directory.Exists(originalPath));
        Assert.IsTrue(File.Exists(Path.Combine(destinationPath, "tracked.txt")));
        Assert.AreEqual(
            GetManagedPath(CanonicalDirectory.Create(destinationPath)),
            GetManagedPath(result.Directory));
        _ = FindWorktree(await CaptureAsync(), destinationPath);
    }

    /// <summary>
    /// Verifies movement rejects an existing directory instead of moving beneath it.
    /// </summary>
    [TestMethod]
    public async Task MoveAsync_WithExistingTarget_RejectsAmbiguousDestination()
    {
        var originalPath = await AddTopicWorktreeAsync("move existing source");
        var destinationPath = Directory.CreateDirectory(
            Path.Combine(_temporaryDirectory!, "move existing destination")).FullName;
        var catalog = await CaptureAsync();

        var exception = await Assert.ThrowsExactlyAsync<WorktreeOperationException>(() =>
            _worktreeService!.MoveAsync(
                CanonicalDirectory.Create(_repositoryPath!),
                catalog,
                FindWorktree(catalog, originalPath),
                destinationPath,
                CancellationToken.None));

        Assert.AreEqual(
            "Choose a new worktree destination that does not already exist.",
            exception.Message);
        Assert.IsTrue(Directory.Exists(originalPath));
        Assert.IsTrue(Directory.Exists(destinationPath));
    }

    /// <summary>
    /// Verifies force removal rechecks every reported path and refuses content changed after confirmation.
    /// </summary>
    [TestMethod]
    public async Task RemoveAsync_WithUntrackedAndIgnoredFiles_RevalidatesThenRequiresForce()
    {
        var targetPath = await AddTopicWorktreeAsync("remove target");
        await File.WriteAllTextAsync(
            Path.Combine(targetPath, ".gitignore"),
            "ignored.txt\n",
            TestContext.Current!.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(targetPath, "ignored.txt"),
            "ignored one\n",
            TestContext.Current.CancellationToken);
        var catalog = await CaptureAsync();
        var plan = await _worktreeService!.PrepareRemovalAsync(
            CanonicalDirectory.Create(_repositoryPath!),
            catalog,
            FindWorktree(catalog, targetPath),
            TestContext.Current.CancellationToken);
        Assert.IsTrue(plan.RequiresForce);
        await File.WriteAllTextAsync(
            Path.Combine(targetPath, "new-after-review.txt"),
            "new path\n",
            TestContext.Current.CancellationToken);

        _ = await Assert.ThrowsExactlyAsync<RepositoryPreconditionException>(() =>
            _worktreeService.RemoveAsync(
                CanonicalDirectory.Create(_repositoryPath!),
                plan,
                force: true,
                TestContext.Current.CancellationToken));

        catalog = await CaptureAsync();
        plan = await _worktreeService.PrepareRemovalAsync(
            CanonicalDirectory.Create(_repositoryPath!),
            catalog,
            FindWorktree(catalog, targetPath),
            TestContext.Current.CancellationToken);
        _ = await _worktreeService.RemoveAsync(
            CanonicalDirectory.Create(_repositoryPath!),
            plan,
            force: true,
            TestContext.Current.CancellationToken);
        Assert.IsFalse(Directory.Exists(targetPath));
        Assert.HasCount(1, (await CaptureAsync()).Worktrees);
    }

    /// <summary>
    /// Verifies a clean linked worktree is removed without force after exact inspection.
    /// </summary>
    [TestMethod]
    public async Task RemoveAsync_WithCleanWorktree_RemovesWithoutForce()
    {
        var targetPath = await AddTopicWorktreeAsync("clean remove target");
        var catalog = await CaptureAsync();
        var plan = await _worktreeService!.PrepareRemovalAsync(
            CanonicalDirectory.Create(_repositoryPath!),
            catalog,
            FindWorktree(catalog, targetPath),
            TestContext.Current!.CancellationToken);
        Assert.IsFalse(plan.RequiresForce);

        _ = await _worktreeService.RemoveAsync(
            CanonicalDirectory.Create(_repositoryPath!),
            plan,
            force: false,
            TestContext.Current.CancellationToken);

        Assert.IsFalse(Directory.Exists(targetPath));
        Assert.HasCount(1, (await CaptureAsync()).Worktrees);
    }

    /// <summary>
    /// Verifies a manually moved linked worktree can be repaired through Git at its new exact path.
    /// </summary>
    [TestMethod]
    public async Task RepairAsync_AfterManualMove_ReconnectsWorktree()
    {
        var originalPath = await AddTopicWorktreeAsync("repair source");
        var repairedPath = Path.Combine(_temporaryDirectory!, "repair destination");
        Directory.Move(originalPath, repairedPath);

        _ = await _worktreeService!.RepairAsync(
            CanonicalDirectory.Create(_repositoryPath!),
            repairedPath,
            TestContext.Current!.CancellationToken);

        _ = FindWorktree(await CaptureAsync(), repairedPath);
        Assert.AreEqual("main content\n", await File.ReadAllTextAsync(
            Path.Combine(repairedPath, "tracked.txt"),
            TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies prune removes only the stale administrative entry shown by Git's unchanged dry run.
    /// </summary>
    [TestMethod]
    public async Task PruneAsync_WithReviewedMissingWorktree_RemovesExactStaleEntry()
    {
        var targetPath = await AddTopicWorktreeAsync("prune target");
        TestDirectory.Delete(targetPath);
        await RunGitAsync(_repositoryPath!, "config", "gc.worktreePruneExpire", "now");
        var plan = await _worktreeService!.PreparePruneAsync(
            CanonicalDirectory.Create(_repositoryPath!),
            TestContext.Current!.CancellationToken);
        Assert.IsTrue(!plan.StandardOutput.IsEmpty || !plan.StandardError.IsEmpty);

        _ = await _worktreeService.PruneAsync(
            CanonicalDirectory.Create(_repositoryPath!),
            plan,
            TestContext.Current.CancellationToken);

        Assert.HasCount(1, (await CaptureAsync()).Worktrees);
    }

    private async Task<string> AddTopicWorktreeAsync(string name)
    {
        var catalog = await CaptureAsync();
        var branch = catalog.Find(RefName.FromBytes("refs/heads/topic"u8));
        Assert.IsNotNull(branch);
        var targetPath = Path.Combine(_temporaryDirectory!, name);
        _ = await _worktreeService!.AddAsync(
            CanonicalDirectory.Create(_repositoryPath!),
            catalog,
            new WorktreeAddRequest(
                targetPath,
                branch!,
                WorktreeAddMode.ExistingBranch,
                NewBranchName: null,
                TrackStartingPoint: false,
                LockAfterCreation: false,
                LockReason: null),
            TestContext.Current!.CancellationToken);
        return targetPath;
    }

    private Task<BranchCatalog> CaptureAsync()
        => _branchService!.CaptureAsync(
            CanonicalDirectory.Create(_repositoryPath!),
            TestContext.Current!.CancellationToken);

    private static WorktreeInfo FindWorktree(BranchCatalog catalog, string path)
    {
        var expected = GetManagedPath(CanonicalDirectory.Create(path));
        return catalog.Worktrees.Single(worktree => string.Equals(
            GetManagedPath(worktree.Path),
            expected,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
    }

    private Task<ProcessResult> CommitAsync(string repositoryPath, string message)
        => RunGitAsync(
            repositoryPath,
            "-c",
            "user.name=GitSail Tests",
            "-c",
            "user.email=gitsail@example.invalid",
            "commit",
            "--quiet",
            "--no-gpg-sign",
            "--message",
            message);

    private async Task<string> RunGitForOutputAsync(string workingDirectory, params string[] arguments)
    {
        var result = await RunGitAsync(workingDirectory, arguments, allowExitOne: true);
        return result.ExitCode == 0 ? Encoding.UTF8.GetString(result.StandardOutput.Span) : string.Empty;
    }

    private Task<ProcessResult> RunGitAsync(string workingDirectory, params string[] arguments)
        => RunGitAsync(workingDirectory, arguments, allowExitOne: false);

    private async Task<ProcessResult> RunGitAsync(
        string workingDirectory,
        string[] arguments,
        bool allowExitOne)
    {
        var invocation = new ProcessInvocation(
            _installation!.Executable,
            [.. arguments.Select(ProcessArgument.Literal)],
            CanonicalDirectory.Create(workingDirectory),
            new GitChildEnvironmentFactory(_processEnvironment!).CreateCheckoutEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(16 * 1024 * 1024, 16 * 1024 * 1024));
        var result = await _runner!.RunAsync(invocation, TestContext.Current!.CancellationToken);
        Assert.IsTrue(
            result.ExitCode == 0 || (allowExitOne && result.ExitCode == 1),
            Encoding.UTF8.GetString(result.StandardError.Span));
        return result;
    }

    private TestProcessEnvironment CreateProcessEnvironment()
        => new(new Dictionary<string, string?>
        {
            ["HOME"] = _temporaryDirectory,
            ["USERPROFILE"] = _temporaryDirectory,
            ["XDG_CONFIG_HOME"] = Path.Combine(_temporaryDirectory!, "xdg-config"),
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["PATH"] = Environment.GetEnvironmentVariable("PATH"),
            ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot"),
            ["WINDIR"] = Environment.GetEnvironmentVariable("WINDIR"),
        });

    private static string GetManagedPath(CanonicalDirectory directory)
        => directory.Kind == NativePathKind.WindowsUtf16
            ? directory.GetWindowsPath()
            : Encoding.UTF8.GetString(directory.GetUnixBytes());

    private static string GetManagedPath(GitPath path)
        => path.Kind == NativePathKind.WindowsUtf16
            ? path.GetWindowsPath()
            : Encoding.UTF8.GetString(path.GetUnixBytes());
}
