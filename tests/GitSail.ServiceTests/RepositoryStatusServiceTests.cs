using GitSail.Domain;
using GitSail.Git.Execution;
using GitSail.Git.Parsing;
using System.Text;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies structured status scanning against isolated real Git repositories.
/// </summary>
[TestClass]
public sealed class RepositoryStatusServiceTests
{
    private string? _temporaryDirectory;
    private GitInstallation? _installation;
    private ChildProcessRunner? _runner;

    /// <summary>
    /// Creates an isolated home and resolves Git for each repository-status test.
    /// </summary>
    [TestInitialize]
    public async Task InitializeAsync()
    {
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"gitsail-status-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);
        _runner = new ChildProcessRunner();
        var resolver = new ExecutableResolver(new RuntimeProcessEnvironment());
        _installation = await new GitVersionService(resolver, _runner).GetAsync(
            CanonicalDirectory.Create(_temporaryDirectory),
            TestContext.Current!.CancellationToken);
    }

    /// <summary>
    /// Removes the isolated repository and home after each test.
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
    /// Verifies staged, unstaged, and untracked status from one generation-stamped scan.
    /// </summary>
    [TestMethod]
    public async Task ScanAsync_WithMixedWorktree_ReturnsStructuredEntries()
    {
        var repositoryPath = Path.Combine(_temporaryDirectory!, "repository");
        await InitializeRepositoryAsync(repositoryPath);
        var trackedPath = Path.Combine(repositoryPath, "tracked.txt");
        File.WriteAllText(trackedPath, "staged\n");
        await RunGitAsync(repositoryPath, "add", "--", "tracked.txt");
        File.AppendAllText(trackedPath, "unstaged\n");
        File.WriteAllText(Path.Combine(repositoryPath, "untracked.txt"), "new\n");
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var environmentFactory = TestProcessEnvironment.CreateGitFactory(_temporaryDirectory!);
        var repository = await new RepositoryDiscoveryService(
            _installation!,
            _runner!,
            environmentFactory).DiscoverAsync(
            workingDirectory,
            TestContext.Current!.CancellationToken);
        var service = new RepositoryStatusService(
            _installation!,
            _runner!,
            environmentFactory,
            new PorcelainV2StatusParser());

        var snapshot = await service.ScanAsync(
            repository,
            workingDirectory,
            new OperationGeneration(12),
            TestContext.Current!.CancellationToken);

        Assert.AreEqual(12L, snapshot.Generation.Value);
        Assert.IsNotNull(snapshot.Precondition);
        Assert.IsNull(snapshot.Precondition.HeadObjectId);
        Assert.AreEqual("refs/heads/main", snapshot.Precondition.HeadName?.DisplayText);
        Assert.AreEqual(32, snapshot.Precondition.IndexFingerprint.Length);
        Assert.IsNull(snapshot.HeadObjectId);
        Assert.AreEqual("main", snapshot.HeadName?.DisplayText);
        Assert.HasCount(2, snapshot.Entries);
        var tracked = snapshot.Entries.Single(static entry => entry.Path.DisplayText == "tracked.txt");
        Assert.AreEqual(GitFileStatus.Added, tracked.IndexStatus);
        Assert.AreEqual(GitFileStatus.Modified, tracked.WorkTreeStatus);
        var untracked = snapshot.Entries.Single(static entry => entry.Path.DisplayText == "untracked.txt");
        Assert.AreEqual(RepositoryStatusEntryKind.Untracked, untracked.Kind);
    }

    /// <summary>
    /// Verifies a real content conflict retains Git's exact base, ours, and theirs stage objects.
    /// </summary>
    [TestMethod]
    public async Task ScanAsync_WithContentConflict_ReturnsExactConflictStages()
    {
        var repositoryPath = Path.Combine(_temporaryDirectory!, "conflict-repository");
        await InitializeRepositoryAsync(repositoryPath);
        const string fileName = "conflict.txt";
        var collisionLine = new string('=', 32);
        var filePath = Path.Combine(repositoryPath, fileName);
        File.WriteAllText(
            Path.Combine(repositoryPath, ".gitattributes"),
            "conflict.txt text eol=crlf\n");
        File.WriteAllText(filePath, $"before\nbase value\n{collisionLine}\nafter\n");
        await RunGitAsync(repositoryPath, "add", "--", ".gitattributes", fileName);
        await CommitAsync(repositoryPath, "base");
        var baseCommit = (await RunGitForOutputAsync(repositoryPath, "rev-parse", "HEAD")).Trim();
        await RunGitAsync(repositoryPath, "branch", "incoming");
        File.WriteAllText(filePath, $"before\nours value\n{collisionLine}\nafter\n");
        await RunGitAsync(repositoryPath, "add", "--", fileName);
        await CommitAsync(repositoryPath, "ours");
        var oursObject = (await RunGitForOutputAsync(repositoryPath, "rev-parse", $"HEAD:{fileName}")).Trim();
        await RunGitAsync(repositoryPath, "switch", "--quiet", "incoming");
        File.WriteAllText(filePath, $"before\ntheirs value\n{collisionLine}\nafter\n");
        await RunGitAsync(repositoryPath, "add", "--", fileName);
        await CommitAsync(repositoryPath, "theirs");
        var theirsObject = (await RunGitForOutputAsync(repositoryPath, "rev-parse", $"HEAD:{fileName}")).Trim();
        var baseObject = (await RunGitForOutputAsync(repositoryPath, "rev-parse", $"{baseCommit}:{fileName}")).Trim();
        await RunGitAsync(repositoryPath, "switch", "--quiet", "main");
        var mergeResult = await RunGitCommandAsync(repositoryPath, "merge", "--no-edit", "incoming");
        Assert.AreEqual(1, mergeResult.ExitCode);
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var environmentFactory = TestProcessEnvironment.CreateGitFactory(_temporaryDirectory!);
        var repository = await new RepositoryDiscoveryService(
            _installation!,
            _runner!,
            environmentFactory).DiscoverAsync(
            workingDirectory,
            TestContext.Current!.CancellationToken);
        var service = new RepositoryStatusService(
            _installation!,
            _runner!,
            environmentFactory,
            new PorcelainV2StatusParser());

        var snapshot = await service.ScanAsync(
            repository,
            workingDirectory,
            new OperationGeneration(3),
            TestContext.Current.CancellationToken);

        var entry = snapshot.Entries.Single(static item => item.Path.DisplayText == fileName);
        Assert.AreEqual(RepositoryStatusEntryKind.Unmerged, entry.Kind);
        Assert.IsNotNull(entry.ConflictStages);
        Assert.AreEqual(baseObject, entry.ConflictStages.Base?.ObjectId.ToString());
        Assert.AreEqual(oursObject, entry.ConflictStages.Ours?.ObjectId.ToString());
        Assert.AreEqual(theirsObject, entry.ConflictStages.Theirs?.ObjectId.ToString());
        Assert.AreEqual(GitFileMode.RegularFile, entry.ConflictStages.Base?.Mode);
        Assert.AreEqual(GitFileMode.RegularFile, entry.ConflictStages.Ours?.Mode);
        Assert.AreEqual(GitFileMode.RegularFile, entry.ConflictStages.Theirs?.Mode);
        var contents = await new ConflictStageContentService(
            _installation!,
            _runner!,
            environmentFactory).LoadAsync(
            workingDirectory,
            entry.ConflictStages,
            TestContext.Current.CancellationToken);
        Assert.AreEqual(
            $"before\nbase value\n{collisionLine}\nafter\n",
            Encoding.UTF8.GetString(contents.Base!.Content!.Value.Span));
        Assert.AreEqual(
            $"before\nours value\n{collisionLine}\nafter\n",
            Encoding.UTF8.GetString(contents.Ours!.Content!.Value.Span));
        Assert.AreEqual(
            $"before\ntheirs value\n{collisionLine}\nafter\n",
            Encoding.UTF8.GetString(contents.Theirs!.Content!.Value.Span));
        var mergeDocument = await new ConflictMergeService(
            _installation!,
            _runner!,
            environmentFactory,
            CreateProcessEnvironment()).MergeAsync(
            workingDirectory,
            contents,
            TestContext.Current.CancellationToken);
        Assert.HasCount(1, mergeDocument.Chunks);
        StringAssert.Contains(
            Encoding.UTF8.GetString(mergeDocument.Content.Span),
            new string('=', 33));
        Assert.AreEqual(
            $"before\nours value\n{collisionLine}\nafter\n",
            Encoding.UTF8.GetString(mergeDocument.BuildResolvedContent([ConflictResolutionChoice.Ours])));
        Assert.AreEqual(
            $"before\ntheirs value\n{collisionLine}\nafter\n",
            Encoding.UTF8.GetString(mergeDocument.BuildResolvedContent([ConflictResolutionChoice.Theirs])));
        Assert.AreEqual(
            $"before\nbase value\n{collisionLine}\nafter\n",
            Encoding.UTF8.GetString(mergeDocument.BuildResolvedContent([ConflictResolutionChoice.Base])));
        Assert.AreEqual(
            $"before\nours value\ntheirs value\n{collisionLine}\nafter\n",
            Encoding.UTF8.GetString(mergeDocument.BuildResolvedContent([ConflictResolutionChoice.Both])));
        var resolvedOurs = mergeDocument.BuildResolvedContent([ConflictResolutionChoice.Ours]);
        using (var rollbackCoordinator = new RepositoryMutationCoordinator())
        {
            var failingService = new ConflictResolutionService(
                _installation!,
                new CheckoutFailingProcessRunner(_runner!),
                environmentFactory,
                rollbackCoordinator);
            _ = await Assert.ThrowsExactlyAsync<GitCommandException>(() => failingService.ResolveAsync(
                repository,
                workingDirectory,
                entry,
                GitFileMode.RegularFile,
                resolvedOurs,
                TestContext.Current.CancellationToken));
        }

        var restoredUnmerged = await RunGitForOutputAsync(repositoryPath, "ls-files", "--unmerged");
        Assert.IsFalse(string.IsNullOrEmpty(restoredUnmerged));
        StringAssert.Contains(File.ReadAllText(filePath), "<<<<<<<", StringComparison.Ordinal);
        using var coordinator = new RepositoryMutationCoordinator();
        _ = await new ConflictResolutionService(
            _installation!,
            _runner!,
            environmentFactory,
            coordinator).ResolveAsync(
            repository,
            workingDirectory,
            entry,
            GitFileMode.RegularFile,
            resolvedOurs,
            TestContext.Current.CancellationToken);

        var unmergedOutput = await RunGitForOutputAsync(repositoryPath, "ls-files", "--unmerged");
        var stagedContent = await RunGitForOutputAsync(repositoryPath, "show", $":{fileName}");
        var expectedClean = $"before\nours value\n{collisionLine}\nafter\n";
        var expectedWorkTree = expectedClean.Replace("\n", "\r\n", StringComparison.Ordinal);
        Assert.AreEqual(string.Empty, unmergedOutput);
        Assert.AreEqual(expectedClean, stagedContent);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(expectedWorkTree), File.ReadAllBytes(filePath));
        if (!OperatingSystem.IsWindows())
        {
            Assert.AreEqual(
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.GroupRead |
                UnixFileMode.OtherRead,
                File.GetUnixFileMode(filePath));
        }
    }

    /// <summary>
    /// Verifies an add/add conflict uses exact temporary-file fallback when the base stage is absent.
    /// </summary>
    [TestMethod]
    public async Task ScanAsync_WithAddAddConflict_MergesWithoutBaseStage()
    {
        var repositoryPath = Path.Combine(_temporaryDirectory!, "add-add-conflict-repository");
        await InitializeRepositoryAsync(repositoryPath);
        File.WriteAllText(Path.Combine(repositoryPath, "seed.txt"), "seed\n");
        await RunGitAsync(repositoryPath, "add", "--", "seed.txt");
        await CommitAsync(repositoryPath, "seed");
        await RunGitAsync(repositoryPath, "branch", "incoming");
        const string fileName = "added file.txt";
        var filePath = Path.Combine(repositoryPath, fileName);
        File.WriteAllText(filePath, "ours added content\n");
        await RunGitAsync(repositoryPath, "add", "--", fileName);
        await CommitAsync(repositoryPath, "ours");
        await RunGitAsync(repositoryPath, "switch", "--quiet", "incoming");
        File.WriteAllText(filePath, "theirs added content\n");
        await RunGitAsync(repositoryPath, "add", "--", fileName);
        await CommitAsync(repositoryPath, "theirs");
        await RunGitAsync(repositoryPath, "switch", "--quiet", "main");
        var mergeResult = await RunGitCommandAsync(repositoryPath, "merge", "--no-edit", "incoming");
        Assert.AreEqual(1, mergeResult.ExitCode);
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var environmentFactory = TestProcessEnvironment.CreateGitFactory(_temporaryDirectory!);
        var repository = await new RepositoryDiscoveryService(
            _installation!,
            _runner!,
            environmentFactory).DiscoverAsync(
            workingDirectory,
            TestContext.Current!.CancellationToken);
        var snapshot = await new RepositoryStatusService(
            _installation!,
            _runner!,
            environmentFactory,
            new PorcelainV2StatusParser()).ScanAsync(
            repository,
            workingDirectory,
            new OperationGeneration(4),
            TestContext.Current.CancellationToken);
        var entry = snapshot.Entries.Single(static item => item.Path.DisplayText == fileName);
        Assert.IsNotNull(entry.ConflictStages);
        Assert.IsNull(entry.ConflictStages.Base);
        var contents = await new ConflictStageContentService(
            _installation!,
            _runner!,
            environmentFactory).LoadAsync(
            workingDirectory,
            entry.ConflictStages,
            TestContext.Current.CancellationToken);

        var document = await new ConflictMergeService(
            _installation!,
            _runner!,
            environmentFactory,
            CreateProcessEnvironment()).MergeAsync(
            workingDirectory,
            contents,
            TestContext.Current.CancellationToken);

        Assert.HasCount(1, document.Chunks);
        Assert.AreEqual(
            "ours added content\n",
            Encoding.UTF8.GetString(document.BuildResolvedContent([ConflictResolutionChoice.Ours])));
        Assert.AreEqual(
            "theirs added content\n",
            Encoding.UTF8.GetString(document.BuildResolvedContent([ConflictResolutionChoice.Theirs])));
        Assert.HasCount(0, document.BuildResolvedContent([ConflictResolutionChoice.Base]));
        using var coordinator = new RepositoryMutationCoordinator();
        _ = await new ConflictResolutionService(
            _installation!,
            _runner!,
            environmentFactory,
            coordinator).ResolveAsync(
            repository,
            workingDirectory,
            entry,
            GitFileMode.RegularFile,
            document.BuildResolvedContent([ConflictResolutionChoice.Theirs]),
            TestContext.Current.CancellationToken);
        Assert.AreEqual("theirs added content\n", File.ReadAllText(filePath));
        Assert.AreEqual(
            string.Empty,
            await RunGitForOutputAsync(repositoryPath, "ls-files", "--unmerged"));
    }

    private async Task RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var result = await RunGitCommandAsync(workingDirectory, arguments);
        Assert.AreEqual(0, result.ExitCode, Encoding.UTF8.GetString(result.StandardError.Span));
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
        await RunGitAsync(repositoryPath, "config", "user.name", "GitSail Tests");
        await RunGitAsync(repositoryPath, "config", "user.email", "gitsail@example.invalid");
    }

    private async Task<string> RunGitForOutputAsync(string workingDirectory, params string[] arguments)
    {
        var result = await RunGitCommandAsync(workingDirectory, arguments);
        Assert.AreEqual(0, result.ExitCode, Encoding.UTF8.GetString(result.StandardError.Span));
        return Encoding.UTF8.GetString(result.StandardOutput.Span);
    }

    private Task CommitAsync(string repositoryPath, string message)
        => RunGitAsync(
            repositoryPath,
            "-c",
            "user.name=GitSail Tests",
            "-c",
            "user.email=gitsail@example.invalid",
            "commit",
            "--quiet",
            "-m",
            message);

    private TestProcessEnvironment CreateProcessEnvironment()
        => new(new Dictionary<string, string?>
        {
            ["HOME"] = _temporaryDirectory,
            ["USERPROFILE"] = _temporaryDirectory,
            ["XDG_CONFIG_HOME"] = Path.Combine(_temporaryDirectory!, "xdg-config"),
            ["XDG_CACHE_HOME"] = Path.Combine(_temporaryDirectory!, "xdg-cache"),
            ["APPDATA"] = Path.Combine(_temporaryDirectory!, "roaming"),
            ["LOCALAPPDATA"] = Path.Combine(_temporaryDirectory!, "local"),
        });

    private async Task<ProcessResult> RunGitCommandAsync(
        string workingDirectory,
        params string[] arguments)
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

        return await _runner!.RunAsync(invocation, TestContext.Current!.CancellationToken);
    }
}
