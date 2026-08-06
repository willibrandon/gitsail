using GitSail.Domain;
using GitSail.Git.Execution;
using System.Text;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies stable branch/worktree capture and revalidated mutations against isolated real repositories.
/// </summary>
[TestClass]
public sealed class BranchServiceTests
{
    private string? _temporaryDirectory;
    private GitInstallation? _installation;
    private ChildProcessRunner? _runner;
    private RepositoryMutationCoordinator? _coordinator;
    private GitChildEnvironmentFactory? _environmentFactory;

    /// <summary>
    /// Creates an isolated home and resolves Git for each branch-service test.
    /// </summary>
    [TestInitialize]
    public async Task InitializeAsync()
    {
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"gitsail-branches-{Guid.NewGuid():N}");
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
    /// Removes the isolated repositories, worktrees, and mutation coordinator after each test.
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
    /// Verifies local, remote-tracking, symbolic, upstream, and linked-worktree state in one stable catalog.
    /// </summary>
    [TestMethod]
    public async Task CaptureAsync_WithTrackingAndLinkedWorktree_ReturnsCompleteExactCatalog()
    {
        var repositoryPath = await CreateRepositoryAsync("capture-repository");
        await RunGitAsync(repositoryPath, "branch", "linked");
        var linkedPath = Path.Combine(_temporaryDirectory!, "linked worktree");
        await RunGitAsync(repositoryPath, "worktree", "add", "--quiet", linkedPath, "linked");
        await ConfigureRemoteRefsAsync(repositoryPath);
        var service = CreateService();

        var catalog = await service.CaptureAsync(
            CanonicalDirectory.Create(repositoryPath),
            TestContext.Current!.CancellationToken);

        var main = GetBranch(catalog, "refs/heads/main");
        Assert.IsTrue(main.IsCurrent);
        Assert.AreEqual("refs/remotes/origin/main", main.UpstreamName?.DisplayText);
        Assert.HasCount(1, main.OccupiedWorktrees);
        var linked = GetBranch(catalog, "refs/heads/linked");
        Assert.IsFalse(linked.IsCurrent);
        Assert.HasCount(1, linked.OccupiedWorktrees);
        Assert.AreEqual(
            CanonicalDirectory.Create(linkedPath).ToString(),
            linked.OccupiedWorktrees[0].DisplayText);
        var remote = GetBranch(catalog, "refs/remotes/origin/team/feature");
        Assert.AreEqual(BranchKind.RemoteTracking, remote.Kind);
        var remoteHead = GetBranch(catalog, "refs/remotes/origin/HEAD");
        Assert.AreEqual("refs/remotes/origin/main", remoteHead.SymbolicTarget?.DisplayText);
        Assert.IsTrue(catalog.Precondition.Matches(catalog.Precondition));
    }

    /// <summary>
    /// Verifies remote checkout preserves the complete branch tail and configures its explicit direct upstream.
    /// </summary>
    [TestMethod]
    public async Task CreateAndSwitchAsync_FromNestedRemoteBranch_PreservesTailAndTracking()
    {
        var repositoryPath = await CreateRepositoryAsync("create-repository");
        await ConfigureRemoteRefsAsync(repositoryPath);
        var service = CreateService();
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var catalog = await service.CaptureAsync(
            workingDirectory,
            TestContext.Current!.CancellationToken);
        var remote = GetBranch(catalog, "refs/remotes/origin/team/feature");
        var proposal = BranchService.GetLocalNameProposal(remote);
        var validatedName = await service.ValidateLocalNameAsync(
            workingDirectory,
            proposal,
            TestContext.Current.CancellationToken);

        _ = await service.CreateAndSwitchAsync(
            workingDirectory,
            catalog,
            validatedName,
            remote,
            trackStartingPoint: true,
            TestContext.Current.CancellationToken);

        var refreshed = await service.CaptureAsync(
            workingDirectory,
            TestContext.Current.CancellationToken);
        var created = GetBranch(refreshed, "refs/heads/team/feature");
        Assert.IsTrue(created.IsCurrent);
        Assert.AreEqual("refs/remotes/origin/team/feature", created.UpstreamName?.DisplayText);
        Assert.AreEqual(
            "refs/remotes/origin/team/feature",
            (await RunGitForOutputAsync(
                repositoryPath,
                "rev-parse",
                "--symbolic-full-name",
                "@{upstream}")).Trim());
    }

    /// <summary>
    /// Verifies exact upstream changes and removal round-trip through refreshed branch catalogs.
    /// </summary>
    [TestMethod]
    public async Task ConfigureUpstreamAsync_WithExactRemoteBranch_SetsChangesAndRemovesTracking()
    {
        var repositoryPath = await CreateRepositoryAsync("upstream-repository");
        await ConfigureRemoteRefsAsync(repositoryPath);
        var service = CreateService();
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var catalog = await service.CaptureAsync(
            workingDirectory,
            TestContext.Current!.CancellationToken);
        var main = GetBranch(catalog, "refs/heads/main");
        var team = GetBranch(catalog, "refs/remotes/origin/team/feature");

        _ = await service.ConfigureUpstreamAsync(
            workingDirectory,
            catalog,
            main,
            team,
            TestContext.Current.CancellationToken);

        catalog = await service.CaptureAsync(workingDirectory, TestContext.Current.CancellationToken);
        main = GetBranch(catalog, "refs/heads/main");
        Assert.AreEqual("refs/remotes/origin/team/feature", main.UpstreamName?.DisplayText);
        Assert.AreEqual(
            "refs/remotes/origin/team/feature",
            (await RunGitForOutputAsync(
                repositoryPath,
                "rev-parse",
                "--symbolic-full-name",
                "main@{upstream}")).Trim());

        _ = await service.ConfigureUpstreamAsync(
            workingDirectory,
            catalog,
            main,
            upstream: null,
            TestContext.Current.CancellationToken);

        catalog = await service.CaptureAsync(workingDirectory, TestContext.Current.CancellationToken);
        main = GetBranch(catalog, "refs/heads/main");
        Assert.IsNull(main.UpstreamName);
        var configuration = await RunGitForOutputAsync(repositoryPath, "config", "--null", "--list");
        Assert.IsFalse(configuration.Contains("branch.main.remote", StringComparison.Ordinal));
        Assert.IsFalse(configuration.Contains("branch.main.merge", StringComparison.Ordinal));
    }

    /// <summary>
    /// Verifies a selected upstream deleted after display is rejected before local tracking configuration changes.
    /// </summary>
    [TestMethod]
    public async Task ConfigureUpstreamAsync_AfterSelectedRemoteRefDeleted_RejectsStaleCatalog()
    {
        var repositoryPath = await CreateRepositoryAsync("stale-upstream-repository");
        await ConfigureRemoteRefsAsync(repositoryPath);
        var service = CreateService();
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var catalog = await service.CaptureAsync(
            workingDirectory,
            TestContext.Current!.CancellationToken);
        var main = GetBranch(catalog, "refs/heads/main");
        var team = GetBranch(catalog, "refs/remotes/origin/team/feature");
        await RunGitAsync(
            repositoryPath,
            "update-ref",
            "-d",
            "refs/remotes/origin/team/feature");

        _ = await Assert.ThrowsExactlyAsync<RepositoryPreconditionException>(() => service.ConfigureUpstreamAsync(
            workingDirectory,
            catalog,
            main,
            team,
            TestContext.Current.CancellationToken));

        Assert.AreEqual(
            "refs/remotes/origin/main",
            (await RunGitForOutputAsync(
                repositoryPath,
                "rev-parse",
                "--symbolic-full-name",
                "main@{upstream}")).Trim());
    }

    /// <summary>
    /// Verifies a branch checked out by a linked worktree is rejected before Git changes the current worktree.
    /// </summary>
    [TestMethod]
    public async Task SwitchAsync_WithLinkedWorktreeOccupancy_RejectsBeforeMutation()
    {
        var repositoryPath = await CreateRepositoryAsync("occupied-repository");
        await RunGitAsync(repositoryPath, "branch", "occupied");
        var linkedPath = Path.Combine(_temporaryDirectory!, "occupied worktree");
        await RunGitAsync(repositoryPath, "worktree", "add", "--quiet", linkedPath, "occupied");
        var service = CreateService();
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var catalog = await service.CaptureAsync(
            workingDirectory,
            TestContext.Current!.CancellationToken);
        var occupied = GetBranch(catalog, "refs/heads/occupied");

        var exception = await Assert.ThrowsExactlyAsync<BranchOperationException>(() => service.SwitchAsync(
            workingDirectory,
            catalog,
            occupied,
            TestContext.Current.CancellationToken));

        StringAssert.Contains(exception.Message, "checked out at");
        Assert.AreEqual(
            "main",
            (await RunGitForOutputAsync(repositoryPath, "branch", "--show-current")).Trim());
    }

    /// <summary>
    /// Verifies a selected branch target changed after display is rejected before checkout.
    /// </summary>
    [TestMethod]
    public async Task SwitchAsync_AfterSelectedRefMoved_RejectsStaleCatalog()
    {
        var repositoryPath = await CreateRepositoryAsync("stale-repository");
        await RunGitAsync(repositoryPath, "branch", "target");
        var service = CreateService();
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var catalog = await service.CaptureAsync(
            workingDirectory,
            TestContext.Current!.CancellationToken);
        var target = GetBranch(catalog, "refs/heads/target");
        File.AppendAllText(Path.Combine(repositoryPath, "tracked.txt"), "second\n");
        await RunGitAsync(repositoryPath, "add", "--", "tracked.txt");
        await CommitAsync(repositoryPath, "second commit");
        await RunGitAsync(repositoryPath, "branch", "--force", "target", "HEAD");

        _ = await Assert.ThrowsExactlyAsync<RepositoryPreconditionException>(() => service.SwitchAsync(
            workingDirectory,
            catalog,
            target,
            TestContext.Current.CancellationToken));

        Assert.AreEqual(
            "main",
            (await RunGitForOutputAsync(repositoryPath, "branch", "--show-current")).Trim());
    }

    /// <summary>
    /// Verifies exact rename, force-delete, and soft-reset operations round-trip through refreshed catalogs.
    /// </summary>
    [TestMethod]
    public async Task RenameDeleteAndResetAsync_WithRefreshedCatalogs_PerformsGitOwnedTransactions()
    {
        var repositoryPath = await CreateRepositoryAsync("mutation-repository");
        var firstCommit = (await RunGitForOutputAsync(repositoryPath, "rev-parse", "HEAD")).Trim();
        await RunGitAsync(repositoryPath, "branch", "disposable");
        File.AppendAllText(Path.Combine(repositoryPath, "tracked.txt"), "second\n");
        await RunGitAsync(repositoryPath, "add", "--", "tracked.txt");
        await CommitAsync(repositoryPath, "second commit");
        var service = CreateService();
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var catalog = await service.CaptureAsync(
            workingDirectory,
            TestContext.Current!.CancellationToken);
        var disposable = GetBranch(catalog, "refs/heads/disposable");
        var renamedName = await service.ValidateLocalNameAsync(
            workingDirectory,
            "archive/disposable",
            TestContext.Current.CancellationToken);

        _ = await service.RenameAsync(
            workingDirectory,
            catalog,
            disposable,
            renamedName,
            TestContext.Current.CancellationToken);
        catalog = await service.CaptureAsync(workingDirectory, TestContext.Current.CancellationToken);
        var renamed = GetBranch(catalog, "refs/heads/archive/disposable");
        _ = await service.DeleteAsync(
            workingDirectory,
            catalog,
            renamed,
            BranchDeleteMode.Force,
            TestContext.Current.CancellationToken);
        catalog = await service.CaptureAsync(workingDirectory, TestContext.Current.CancellationToken);
        Assert.IsNull(catalog.Find(RefName.FromBytes("refs/heads/archive/disposable"u8)));
        var current = GetBranch(catalog, "refs/heads/main");
        Assert.IsTrue(ObjectId.TryParseHex(Encoding.ASCII.GetBytes(firstCommit), out var targetObjectId));

        _ = await service.ResetCurrentAsync(
            workingDirectory,
            catalog,
            current,
            targetObjectId!,
            BranchResetMode.Soft,
            TestContext.Current.CancellationToken);

        Assert.AreEqual(firstCommit, (await RunGitForOutputAsync(repositoryPath, "rev-parse", "HEAD")).Trim());
        Assert.AreEqual("baseline\nsecond\n", File.ReadAllText(Path.Combine(repositoryPath, "tracked.txt")));
    }

    private BranchService CreateService()
        => new(_installation!, _runner!, _environmentFactory!, _coordinator!);

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
        File.WriteAllText(Path.Combine(repositoryPath, "tracked.txt"), "baseline\n");
        await RunGitAsync(repositoryPath, "add", "--", "tracked.txt");
        await CommitAsync(repositoryPath, "baseline");
        return repositoryPath;
    }

    private async Task ConfigureRemoteRefsAsync(string repositoryPath)
    {
        var head = (await RunGitForOutputAsync(repositoryPath, "rev-parse", "HEAD")).Trim();
        await RunGitAsync(repositoryPath, "config", "remote.origin.url", repositoryPath);
        await RunGitAsync(
            repositoryPath,
            "config",
            "remote.origin.fetch",
            "+refs/heads/*:refs/remotes/origin/*");
        await RunGitAsync(repositoryPath, "update-ref", "refs/remotes/origin/main", head);
        await RunGitAsync(repositoryPath, "update-ref", "refs/remotes/origin/team/feature", head);
        await RunGitAsync(
            repositoryPath,
            "symbolic-ref",
            "refs/remotes/origin/HEAD",
            "refs/remotes/origin/main");
        await RunGitAsync(repositoryPath, "config", "branch.main.remote", "origin");
        await RunGitAsync(repositoryPath, "config", "branch.main.merge", "refs/heads/main");
    }

    private async Task CommitAsync(string repositoryPath, string message)
        => await RunGitAsync(
            repositoryPath,
            "-c",
            "user.name=GitSail Tests",
            "-c",
            "user.email=gitsail@example.invalid",
            "commit",
            "--quiet",
            "-m",
            message);

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
            OutputPolicy.Create(1024 * 1024, 1024 * 1024));
        var result = await _runner!.RunAsync(invocation, TestContext.Current!.CancellationToken);

        Assert.AreEqual(0, result.ExitCode, Encoding.UTF8.GetString(result.StandardError.Span));
        return Encoding.UTF8.GetString(result.StandardOutput.Span);
    }

    private static BranchInfo GetBranch(BranchCatalog catalog, string fullName)
    {
        var branch = catalog.Branches.SingleOrDefault(
            branch => string.Equals(branch.FullName.DisplayText, fullName, StringComparison.Ordinal));
        Assert.IsNotNull(branch, $"Expected branch '{fullName}'.");
        return branch;
    }
}
