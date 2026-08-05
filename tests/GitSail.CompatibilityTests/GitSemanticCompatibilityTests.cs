using System.Text;
using GitSail.Domain;
using GitSail.Git.Execution;
using GitSail.Git.Parsing;

namespace GitSail.CompatibilityTests;

/// <summary>
/// Compares GitSail transactions with equivalent Git reference transactions in twin repositories.
/// </summary>
[TestClass]
public sealed class GitSemanticCompatibilityTests
{
    private string? _temporaryDirectory;
    private ChildProcessRunner? _runner;
    private GitInstallation? _installation;
    private GitChildEnvironmentFactory? _environmentFactory;

    /// <summary>
    /// Creates one isolated deterministic environment and resolves Git before each comparison.
    /// </summary>
    [TestInitialize]
    public async Task InitializeAsync()
    {
        _temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"gitsail-compatibility-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);
        _runner = new ChildProcessRunner();
        _environmentFactory = new GitChildEnvironmentFactory(
            new CompatibilityProcessEnvironment(_temporaryDirectory));
        _installation = await new GitVersionService(
            new ExecutableResolver(new RuntimeProcessEnvironment()),
            _runner).GetAsync(
                CanonicalDirectory.Create(_temporaryDirectory),
                TestContext.Current!.CancellationToken);
    }

    /// <summary>
    /// Removes every repository and configuration file owned by the completed comparison.
    /// </summary>
    [TestCleanup]
    public void Cleanup()
    {
        if (_temporaryDirectory is null || !Directory.Exists(_temporaryDirectory))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            var options = new EnumerationOptions
            {
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false,
                RecurseSubdirectories = true,
                ReturnSpecialDirectories = false,
            };
            foreach (var entry in new DirectoryInfo(_temporaryDirectory).EnumerateFileSystemInfos("*", options))
            {
                entry.Attributes &= ~FileAttributes.ReadOnly;
            }
        }

        Directory.Delete(_temporaryDirectory, recursive: true);
    }

    /// <summary>
    /// Verifies selected-path staging produces the same index, patch, status, and HEAD as Git add.
    /// </summary>
    [TestMethod]
    public async Task StageAsync_WithMixedWorkingTree_ProducesGitAddSemantics()
    {
        var (referencePath, subjectPath) = await CreateTwinRepositoriesAsync("stage");
        ApplyMixedChanges(referencePath);
        ApplyMixedChanges(subjectPath);
        await RunGitAsync(referencePath, "add", "--", "tracked.txt", "added.txt");
        var workingDirectory = CanonicalDirectory.Create(subjectPath);
        var snapshot = await ScanAsync(workingDirectory, new OperationGeneration(1));
        var selectedPaths = snapshot.Entries
            .Where(static entry => entry.Path.DisplayText is "tracked.txt" or "added.txt")
            .Select(static entry => entry.Path)
            .ToArray();
        using var coordinator = new RepositoryMutationCoordinator();
        var service = new IndexMutationService(
            _installation!,
            _runner!,
            _environmentFactory!,
            coordinator);

        _ = await service.StageAsync(
            workingDirectory,
            selectedPaths,
            TestContext.Current!.CancellationToken);

        await AssertRepositorySemanticsEqualAsync(referencePath, subjectPath);
    }

    /// <summary>
    /// Verifies unstage-all produces the same index, patch, status, and HEAD as Git reset mixed.
    /// </summary>
    [TestMethod]
    public async Task UnstageAllAsync_WithMixedIndex_ProducesGitResetMixedSemantics()
    {
        var (referencePath, subjectPath) = await CreateTwinRepositoriesAsync("unstage-all");
        ApplyMixedChanges(referencePath);
        ApplyMixedChanges(subjectPath);
        await RunGitAsync(referencePath, "add", "--all", "--");
        await RunGitAsync(subjectPath, "add", "--all", "--");
        await RunGitAsync(referencePath, "reset", "--mixed", "--quiet", "HEAD", "--");
        var workingDirectory = CanonicalDirectory.Create(subjectPath);
        var snapshot = await ScanAsync(workingDirectory, new OperationGeneration(1));
        using var coordinator = new RepositoryMutationCoordinator();
        var service = new IndexMutationService(
            _installation!,
            _runner!,
            _environmentFactory!,
            coordinator);

        _ = await service.UnstageAllAsync(
            snapshot,
            workingDirectory,
            TestContext.Current!.CancellationToken);

        await AssertRepositorySemanticsEqualAsync(referencePath, subjectPath);
    }

    /// <summary>
    /// Verifies create-and-switch produces the same local refs, HEAD attachment, and worktree as Git switch.
    /// </summary>
    [TestMethod]
    public async Task CreateAndSwitchAsync_FromLocalBranch_ProducesGitSwitchSemantics()
    {
        var (referencePath, subjectPath) = await CreateTwinRepositoriesAsync("branch");
        await RunGitAsync(
            referencePath,
            "switch",
            "--no-track",
            "--create",
            "feature/compatibility",
            "refs/heads/main");
        var workingDirectory = CanonicalDirectory.Create(subjectPath);
        using var coordinator = new RepositoryMutationCoordinator();
        var service = new BranchService(
            _installation!,
            _runner!,
            _environmentFactory!,
            coordinator);
        var catalog = await service.CaptureAsync(
            workingDirectory,
            TestContext.Current!.CancellationToken);
        var main = catalog.Branches.Single(static branch => branch.FullName.DisplayText == "refs/heads/main");
        var name = await service.ValidateLocalNameAsync(
            workingDirectory,
            "feature/compatibility",
            TestContext.Current.CancellationToken);

        _ = await service.CreateAndSwitchAsync(
            workingDirectory,
            catalog,
            name,
            main,
            trackStartingPoint: false,
            TestContext.Current.CancellationToken);

        await AssertRepositorySemanticsEqualAsync(referencePath, subjectPath);
        await AssertGitOutputEqualAsync(
            referencePath,
            subjectPath,
            "for-each-ref",
            "--format=%(refname)%00%(objectname)%00",
            "refs/heads");
        await AssertGitOutputEqualAsync(referencePath, subjectPath, "symbolic-ref", "HEAD");
    }

    /// <summary>
    /// Verifies stash creation produces the same commit graph and cleaned worktree as Git stash push.
    /// </summary>
    [TestMethod]
    public async Task CreateStashAsync_WithUntrackedFiles_ProducesGitStashPushSemantics()
    {
        var (referencePath, subjectPath) = await CreateTwinRepositoriesAsync("stash");
        ApplyMixedChanges(referencePath);
        ApplyMixedChanges(subjectPath);
        await RunGitAsync(
            referencePath,
            "stash",
            "push",
            "--include-untracked",
            "--message",
            "compatibility stash",
            "--");
        var workingDirectory = CanonicalDirectory.Create(subjectPath);
        using var coordinator = new RepositoryMutationCoordinator();
        var service = new StashService(
            _installation!,
            _runner!,
            _environmentFactory!,
            coordinator);
        var catalog = await service.CaptureAsync(
            workingDirectory,
            TestContext.Current!.CancellationToken);

        _ = await service.CreateAsync(
            workingDirectory,
            catalog.Precondition,
            new StashCreateOptions(
                "compatibility stash",
                StashFileScope.IncludeUntracked,
                keepIndex: false,
                stagedOnly: false),
            TestContext.Current.CancellationToken);

        await AssertRepositorySemanticsEqualAsync(referencePath, subjectPath);
        await AssertGitOutputEqualAsync(referencePath, subjectPath, "rev-parse", "refs/stash");
        await AssertGitOutputEqualAsync(referencePath, subjectPath, "rev-parse", "refs/stash^{tree}");
        await AssertGitOutputEqualAsync(referencePath, subjectPath, "rev-parse", "refs/stash^2^{tree}");
        await AssertGitOutputEqualAsync(referencePath, subjectPath, "rev-parse", "refs/stash^3^{tree}");
    }

    private async Task<(string ReferencePath, string SubjectPath)> CreateTwinRepositoriesAsync(string name)
    {
        var referencePath = Path.Combine(_temporaryDirectory!, $"{name}-reference");
        var subjectPath = Path.Combine(_temporaryDirectory!, $"{name}-subject");
        await InitializeRepositoryAsync(referencePath);
        await InitializeRepositoryAsync(subjectPath);
        await AssertRepositorySemanticsEqualAsync(referencePath, subjectPath);
        return (referencePath, subjectPath);
    }

    private async Task InitializeRepositoryAsync(string repositoryPath)
    {
        await RunGitAsync(
            _temporaryDirectory!,
            "init",
            "--quiet",
            "--initial-branch=main",
            "--",
            repositoryPath);
        File.WriteAllText(Path.Combine(repositoryPath, "tracked.txt"), "baseline\n");
        File.WriteAllText(Path.Combine(repositoryPath, "removed.txt"), "remove me\n");
        await RunGitAsync(repositoryPath, "add", "--all", "--");
        await RunGitAsync(repositoryPath, "commit", "--quiet", "--message", "baseline", "--");
    }

    private static void ApplyMixedChanges(string repositoryPath)
    {
        File.WriteAllText(Path.Combine(repositoryPath, "tracked.txt"), "changed\n");
        File.WriteAllText(Path.Combine(repositoryPath, "added.txt"), "added\n");
        File.Delete(Path.Combine(repositoryPath, "removed.txt"));
    }

    private async Task<RepositoryStatusSnapshot> ScanAsync(
        CanonicalDirectory workingDirectory,
        OperationGeneration generation)
    {
        var repository = await new RepositoryDiscoveryService(
            _installation!,
            _runner!,
            _environmentFactory!).DiscoverAsync(
                workingDirectory,
                TestContext.Current!.CancellationToken);
        return await new RepositoryStatusService(
            _installation!,
            _runner!,
            _environmentFactory!,
            new PorcelainV2StatusParser()).ScanAsync(
                repository,
                workingDirectory,
                generation,
                TestContext.Current.CancellationToken);
    }

    private async Task AssertRepositorySemanticsEqualAsync(string referencePath, string subjectPath)
    {
        await AssertGitOutputEqualAsync(referencePath, subjectPath, "rev-parse", "HEAD");
        await AssertGitOutputEqualAsync(referencePath, subjectPath, "write-tree");
        await AssertGitOutputEqualAsync(
            referencePath,
            subjectPath,
            "status",
            "--porcelain=v2",
            "-z",
            "--branch",
            "--untracked-files=all",
            "--renames",
            "--");
        await AssertGitOutputEqualAsync(
            referencePath,
            subjectPath,
            "diff",
            "--cached",
            "--binary",
            "--full-index",
            "--no-color",
            "--no-ext-diff",
            "--no-textconv",
            "--");
    }

    private async Task AssertGitOutputEqualAsync(
        string referencePath,
        string subjectPath,
        params string[] arguments)
    {
        var reference = await RunGitForOutputAsync(referencePath, arguments);
        var subject = await RunGitForOutputAsync(subjectPath, arguments);
        CollectionAssert.AreEqual(reference, subject, $"Git output differed for: {string.Join(' ', arguments)}");
    }

    private async Task RunGitAsync(string workingDirectory, params string[] arguments)
        => _ = await RunGitForOutputAsync(workingDirectory, arguments);

    private async Task<byte[]> RunGitForOutputAsync(string workingDirectory, params string[] arguments)
    {
        var invocation = new ProcessInvocation(
            _installation!.Executable,
            [.. arguments.Select(ProcessArgument.Literal)],
            CanonicalDirectory.Create(workingDirectory),
            _environmentFactory!.CreateCommitEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(128 * 1024 * 1024, 16 * 1024 * 1024));
        var result = await _runner!.RunAsync(
            invocation,
            TestContext.Current!.CancellationToken);
        Assert.AreEqual(
            0,
            result.ExitCode,
            Encoding.UTF8.GetString(result.StandardError.Span));
        return result.StandardOutput.ToArray();
    }
}
