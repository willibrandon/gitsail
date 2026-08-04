using GitSail.Domain;
using GitSail.Git.Execution;
using GitSail.Git.Parsing;
using System.Text;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies exact path staging and unstaging against isolated real Git repositories.
/// </summary>
[TestClass]
public sealed class IndexMutationServiceTests
{
    private string? _temporaryDirectory;
    private GitInstallation? _installation;
    private ChildProcessRunner? _runner;

    /// <summary>
    /// Creates an isolated home and resolves Git for each index-mutation test.
    /// </summary>
    [TestInitialize]
    public async Task InitializeAsync()
    {
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"gitsail-index-{Guid.NewGuid():N}");
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
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    /// <summary>
    /// Verifies literal option-looking and space-containing paths through stage and unborn unstage.
    /// </summary>
    [TestMethod]
    public async Task StageAndUnstageAsync_WithHostilePathShapes_RoundTripsExactSelection()
    {
        var repositoryPath = Path.Combine(_temporaryDirectory!, "repository");
        await RunGitAsync(_temporaryDirectory!, "init", "--quiet", "--initial-branch=main", "--", repositoryPath);
        File.WriteAllText(Path.Combine(repositoryPath, "--option-like.txt"), "option\n");
        File.WriteAllText(Path.Combine(repositoryPath, "file with spaces.txt"), "spaces\n");
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var environmentFactory = TestProcessEnvironment.CreateGitFactory(_temporaryDirectory!);
        var repository = await new RepositoryDiscoveryService(
            _installation!,
            _runner!,
            environmentFactory).DiscoverAsync(
            workingDirectory,
            TestContext.Current!.CancellationToken);
        var statusService = new RepositoryStatusService(
            _installation!,
            _runner!,
            environmentFactory,
            new PorcelainV2StatusParser());
        var initial = await statusService.ScanAsync(
            repository,
            workingDirectory,
            new OperationGeneration(1),
            TestContext.Current.CancellationToken);
        var paths = initial.Entries.Select(static entry => entry.Path).ToArray();
        using var coordinator = new RepositoryMutationCoordinator();
        var service = new IndexMutationService(
            _installation!,
            _runner!,
            environmentFactory,
            coordinator);

        _ = await service.StageAsync(workingDirectory, paths, TestContext.Current.CancellationToken);
        var staged = await statusService.ScanAsync(
            repository,
            workingDirectory,
            new OperationGeneration(2),
            TestContext.Current.CancellationToken);

        Assert.HasCount(2, staged.Entries);
        Assert.IsTrue(staged.Entries.All(static entry => entry.IndexStatus == GitFileStatus.Added));

        _ = await service.UnstageAsync(staged, workingDirectory, paths, TestContext.Current.CancellationToken);
        var unstaged = await statusService.ScanAsync(
            repository,
            workingDirectory,
            new OperationGeneration(3),
            TestContext.Current.CancellationToken);

        Assert.HasCount(2, unstaged.Entries);
        Assert.IsTrue(unstaged.Entries.All(static entry => entry.Kind == RepositoryStatusEntryKind.Untracked));
    }

    /// <summary>
    /// Verifies stage-all includes additions, modifications, and deletions before unstage-all restores HEAD.
    /// </summary>
    [TestMethod]
    public async Task StageAllAndUnstageAllAsync_WithMixedChanges_RoundTripsCompleteIndex()
    {
        var repositoryPath = Path.Combine(_temporaryDirectory!, "all-repository");
        await RunGitAsync(_temporaryDirectory!, "init", "--quiet", "--initial-branch=main", "--", repositoryPath);
        File.WriteAllText(Path.Combine(repositoryPath, "modified.txt"), "baseline\n");
        File.WriteAllText(Path.Combine(repositoryPath, "deleted.txt"), "delete me\n");
        await RunGitAsync(repositoryPath, "add", "--all");
        await RunGitAsync(
            repositoryPath,
            "-c",
            "user.name=GitSail Tests",
            "-c",
            "user.email=gitsail@example.invalid",
            "commit",
            "--quiet",
            "-m",
            "baseline");
        File.WriteAllText(Path.Combine(repositoryPath, "modified.txt"), "changed\n");
        File.Delete(Path.Combine(repositoryPath, "deleted.txt"));
        File.WriteAllText(Path.Combine(repositoryPath, "added.txt"), "new\n");
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var environmentFactory = TestProcessEnvironment.CreateGitFactory(_temporaryDirectory!);
        var repository = await new RepositoryDiscoveryService(
            _installation!,
            _runner!,
            environmentFactory).DiscoverAsync(
            workingDirectory,
            TestContext.Current!.CancellationToken);
        var statusService = new RepositoryStatusService(
            _installation!,
            _runner!,
            environmentFactory,
            new PorcelainV2StatusParser());
        var initial = await statusService.ScanAsync(
            repository,
            workingDirectory,
            new OperationGeneration(1),
            TestContext.Current.CancellationToken);
        using var coordinator = new RepositoryMutationCoordinator();
        var service = new IndexMutationService(
            _installation!,
            _runner!,
            environmentFactory,
            coordinator);

        _ = await service.StageAllAsync(workingDirectory, TestContext.Current.CancellationToken);
        var staged = await statusService.ScanAsync(
            repository,
            workingDirectory,
            new OperationGeneration(2),
            TestContext.Current.CancellationToken);

        Assert.HasCount(3, staged.Entries);
        Assert.IsTrue(staged.Entries.All(static entry => entry.IndexStatus != GitFileStatus.Unmodified));
        Assert.IsTrue(staged.Entries.All(static entry => entry.WorkTreeStatus == GitFileStatus.Unmodified));

        _ = await service.UnstageAllAsync(staged, workingDirectory, TestContext.Current.CancellationToken);
        var unstaged = await statusService.ScanAsync(
            repository,
            workingDirectory,
            new OperationGeneration(3),
            TestContext.Current.CancellationToken);

        Assert.HasCount(3, unstaged.Entries);
        Assert.IsTrue(unstaged.Entries.All(static entry => entry.IndexStatus == GitFileStatus.Unmodified));
        Assert.IsTrue(unstaged.Entries.All(static entry => entry.WorkTreeStatus != GitFileStatus.Unmodified ||
            entry.Kind == RepositoryStatusEntryKind.Untracked));
        Assert.AreEqual(initial.HeadObjectId, unstaged.HeadObjectId);
    }

    /// <summary>
    /// Verifies typed intent-to-add exposes an untracked patch whose selected line can be staged exactly.
    /// </summary>
    [TestMethod]
    public async Task PrepareIntentToAddAsync_WithUntrackedPath_EnablesExactLineStaging()
    {
        var repositoryPath = Path.Combine(_temporaryDirectory!, "intent-to-add-repository");
        await RunGitAsync(_temporaryDirectory!, "init", "--quiet", "--initial-branch=main", "--", repositoryPath);
        File.WriteAllText(Path.Combine(repositoryPath, "tracked.txt"), "baseline\n");
        await RunGitAsync(repositoryPath, "add", "--", "tracked.txt");
        await RunGitAsync(
            repositoryPath,
            "-c",
            "user.name=GitSail Tests",
            "-c",
            "user.email=gitsail@example.invalid",
            "commit",
            "--quiet",
            "-m",
            "baseline");
        const string fileName = "untracked lines.txt";
        File.WriteAllText(Path.Combine(repositoryPath, fileName), "first line\nsecond line\nthird line\n");
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var environmentFactory = TestProcessEnvironment.CreateGitFactory(_temporaryDirectory!);
        var statusService = new RepositoryStatusService(
            _installation!,
            _runner!,
            environmentFactory,
            new PorcelainV2StatusParser());
        var repository = await new RepositoryDiscoveryService(
            _installation!,
            _runner!,
            environmentFactory).DiscoverAsync(
            workingDirectory,
            TestContext.Current!.CancellationToken);
        var initial = await statusService.ScanAsync(
            repository,
            workingDirectory,
            new OperationGeneration(1),
            TestContext.Current.CancellationToken);
        var initialEntry = initial.Entries.Single(entry => entry.Path.Equals(CreatePath(fileName)));
        Assert.AreEqual(RepositoryStatusEntryKind.Untracked, initialEntry.Kind);
        using var coordinator = new RepositoryMutationCoordinator();
        var indexService = new IndexMutationService(
            _installation!,
            _runner!,
            environmentFactory,
            coordinator);

        _ = await indexService.PrepareIntentToAddAsync(
            workingDirectory,
            [CreatePath(fileName)],
            TestContext.Current.CancellationToken);
        var prepared = await statusService.ScanAsync(
            repository,
            workingDirectory,
            new OperationGeneration(2),
            TestContext.Current.CancellationToken);
        var preparedEntry = prepared.Entries.Single(entry => entry.Path.Equals(CreatePath(fileName)));
        Assert.AreEqual(RepositoryStatusEntryKind.Ordinary, preparedEntry.Kind);
        var rawDiffService = new RawDiffService(_installation!, _runner!, environmentFactory);
        using var workTreeDiff = await rawDiffService.CaptureAsync(
            workingDirectory,
            RawDiffTarget.WorkTree,
            new OperationGeneration(2),
            TestContext.Current.CancellationToken);
        var workTreeFile = workTreeDiff.Index.Find(CreatePath(fileName));
        Assert.IsNotNull(workTreeFile);
        var firstAddition = workTreeFile.PatchIndex.Hunks
            .SelectMany(static hunk => hunk.Lines)
            .First(static line => line.Kind == RawPatchLineKind.Addition);
        var selectedPatch = await workTreeDiff.ReadSelectedLinesPatchAsync(
            workTreeFile,
            new HashSet<int> { firstAddition.LineNumber },
            RawPatchSelectionSide.PreserveOldSide,
            TestContext.Current.CancellationToken);
        var patchService = new RepositoryPatchService(
            _installation!,
            _runner!,
            environmentFactory,
            coordinator);

        _ = await patchService.StageAsync(
            workingDirectory,
            selectedPatch,
            TestContext.Current.CancellationToken);
        using var indexDiff = await rawDiffService.CaptureAsync(
            workingDirectory,
            RawDiffTarget.Index,
            new OperationGeneration(3),
            TestContext.Current.CancellationToken);
        var indexFile = indexDiff.Index.Find(CreatePath(fileName));
        Assert.IsNotNull(indexFile);
        var indexPatch = Encoding.UTF8.GetString(await indexDiff.ReadFileAsync(
            indexFile,
            TestContext.Current.CancellationToken));

        StringAssert.Contains(indexPatch, "+first line");
        Assert.IsFalse(indexPatch.Contains("+second line", StringComparison.Ordinal));
        Assert.AreEqual(
            "first line\nsecond line\nthird line\n",
            File.ReadAllText(Path.Combine(repositoryPath, fileName)));
    }

    /// <summary>
    /// Verifies exact complete-hunk patches pass preflight and round-trip through cached apply.
    /// </summary>
    [TestMethod]
    public async Task StageAndUnstagePatchAsync_WithOneOfTwoHunks_MutatesOnlySelectedHunk()
    {
        var repositoryPath = Path.Combine(_temporaryDirectory!, "patch-repository");
        await RunGitAsync(_temporaryDirectory!, "init", "--quiet", "--initial-branch=main", "--", repositoryPath);
        const string fileName = "file with spaces.txt";
        var filePath = Path.Combine(repositoryPath, fileName);
        var baselineLines = Enumerable.Range(1, 24).Select(static line => $"line {line}").ToArray();
        File.WriteAllText(filePath, string.Join('\n', baselineLines) + "\n");
        await RunGitAsync(repositoryPath, "add", "--", fileName);
        await RunGitAsync(
            repositoryPath,
            "-c",
            "user.name=GitSail Tests",
            "-c",
            "user.email=gitsail@example.invalid",
            "commit",
            "--quiet",
            "-m",
            "baseline");
        var changedLines = baselineLines.ToArray();
        changedLines[1] = "changed first hunk";
        changedLines[21] = "changed second hunk";
        File.WriteAllText(filePath, string.Join('\n', changedLines) + "\n");
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var environmentFactory = TestProcessEnvironment.CreateGitFactory(_temporaryDirectory!);
        var rawDiffService = new RawDiffService(_installation!, _runner!, environmentFactory);
        using var rawDiff = await rawDiffService.CaptureAsync(
            workingDirectory,
            RawDiffTarget.WorkTree,
            new OperationGeneration(1),
            TestContext.Current!.CancellationToken);
        var rawFile = rawDiff.Index.Find(CreatePath(fileName));
        Assert.IsNotNull(rawFile);
        var patch = await rawDiff.ReadFileAsync(rawFile, TestContext.Current.CancellationToken);
        var patchIndex = RawPatchParser.Parse(patch);
        Assert.HasCount(2, patchIndex.Hunks);
        var selectedPatch = RawPatchSelectionBuilder.BuildSingleHunk(
            patch,
            patchIndex,
            patchIndex.Hunks[0]);
        using var coordinator = new RepositoryMutationCoordinator();
        var mutationService = new RepositoryPatchService(
            _installation!,
            _runner!,
            environmentFactory,
            coordinator);

        _ = await mutationService.StageAsync(
            workingDirectory,
            selectedPatch,
            TestContext.Current.CancellationToken);
        var cachedPatch = await CaptureSingleFilePatchAsync(
            rawDiffService,
            workingDirectory,
            RawDiffTarget.Index,
            new OperationGeneration(2),
            fileName);
        StringAssert.Contains(Encoding.UTF8.GetString(cachedPatch), "+changed first hunk");
        Assert.IsFalse(Encoding.UTF8.GetString(cachedPatch).Contains(
            "+changed second hunk",
            StringComparison.Ordinal));

        _ = await mutationService.UnstageAsync(
            workingDirectory,
            selectedPatch,
            TestContext.Current.CancellationToken);
        using var cachedAfterUnstage = await rawDiffService.CaptureAsync(
            workingDirectory,
            RawDiffTarget.Index,
            new OperationGeneration(3),
            TestContext.Current.CancellationToken);

        Assert.HasCount(0, cachedAfterUnstage.Index.Files);
    }

    /// <summary>
    /// Verifies forward and reverse selected-line patches round-trip one replacement inside a shared hunk.
    /// </summary>
    [TestMethod]
    public async Task StageAndUnstagePatchAsync_WithSelectedLines_MutatesOnlyRequestedReplacement()
    {
        var repositoryPath = Path.Combine(_temporaryDirectory!, "line-patch-repository");
        await RunGitAsync(_temporaryDirectory!, "init", "--quiet", "--initial-branch=main", "--", repositoryPath);
        const string fileName = "selected lines.txt";
        var filePath = Path.Combine(repositoryPath, fileName);
        var baselineLines = Enumerable.Range(1, 12).Select(static line => $"line {line}").ToArray();
        File.WriteAllText(filePath, string.Join('\n', baselineLines) + "\n");
        await RunGitAsync(repositoryPath, "add", "--", fileName);
        await RunGitAsync(
            repositoryPath,
            "-c",
            "user.name=GitSail Tests",
            "-c",
            "user.email=gitsail@example.invalid",
            "commit",
            "--quiet",
            "-m",
            "baseline");
        var changedLines = baselineLines.ToArray();
        changedLines[3] = "selected replacement";
        changedLines[6] = "unselected replacement";
        File.WriteAllText(filePath, string.Join('\n', changedLines) + "\n");
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var environmentFactory = TestProcessEnvironment.CreateGitFactory(_temporaryDirectory!);
        var rawDiffService = new RawDiffService(_installation!, _runner!, environmentFactory);
        using var workTreeDiff = await rawDiffService.CaptureAsync(
            workingDirectory,
            RawDiffTarget.WorkTree,
            new OperationGeneration(1),
            TestContext.Current!.CancellationToken);
        var workTreeFile = workTreeDiff.Index.Find(CreatePath(fileName));
        Assert.IsNotNull(workTreeFile);
        Assert.HasCount(1, workTreeFile.PatchIndex.Hunks);
        var selectedWorkTreeLines = workTreeFile.PatchIndex.Hunks[0].Lines
            .Where(static line => line.Kind is RawPatchLineKind.Deletion or RawPatchLineKind.Addition)
            .Take(2)
            .Select(static line => line.LineNumber)
            .ToHashSet();
        var stagePatch = await workTreeDiff.ReadSelectedLinesPatchAsync(
            workTreeFile,
            selectedWorkTreeLines,
            RawPatchSelectionSide.PreserveOldSide,
            TestContext.Current.CancellationToken);
        using var coordinator = new RepositoryMutationCoordinator();
        var mutationService = new RepositoryPatchService(
            _installation!,
            _runner!,
            environmentFactory,
            coordinator);

        _ = await mutationService.StageAsync(
            workingDirectory,
            stagePatch,
            TestContext.Current.CancellationToken);
        using var indexDiff = await rawDiffService.CaptureAsync(
            workingDirectory,
            RawDiffTarget.Index,
            new OperationGeneration(2),
            TestContext.Current.CancellationToken);
        var indexFile = indexDiff.Index.Find(CreatePath(fileName));
        Assert.IsNotNull(indexFile);
        var indexPatchBytes = await indexDiff.ReadFileAsync(indexFile, TestContext.Current.CancellationToken);
        var indexPatchText = Encoding.UTF8.GetString(indexPatchBytes);
        StringAssert.Contains(indexPatchText, "+selected replacement");
        Assert.IsFalse(indexPatchText.Contains("+unselected replacement", StringComparison.Ordinal));

        var selectedIndexLines = indexFile.PatchIndex.Hunks
            .SelectMany(static hunk => hunk.Lines)
            .Where(static line => line.Kind is RawPatchLineKind.Deletion or RawPatchLineKind.Addition)
            .Select(static line => line.LineNumber)
            .ToHashSet();
        var unstagePatch = await indexDiff.ReadSelectedLinesPatchAsync(
            indexFile,
            selectedIndexLines,
            RawPatchSelectionSide.PreserveNewSide,
            TestContext.Current.CancellationToken);
        _ = await mutationService.UnstageAsync(
            workingDirectory,
            unstagePatch,
            TestContext.Current.CancellationToken);
        using var indexAfterUnstage = await rawDiffService.CaptureAsync(
            workingDirectory,
            RawDiffTarget.Index,
            new OperationGeneration(3),
            TestContext.Current.CancellationToken);

        Assert.HasCount(0, indexAfterUnstage.Index.Files);
        Assert.AreEqual(string.Join('\n', changedLines) + "\n", File.ReadAllText(filePath));
    }

    /// <summary>
    /// Verifies selected-line revert and one-level undo preserve every unselected worktree byte.
    /// </summary>
    [TestMethod]
    public async Task RevertAndUndoRevertAsync_WithSelectedLines_RoundTripsExactWorkTreeContent()
    {
        var repositoryPath = Path.Combine(_temporaryDirectory!, "revert-patch-repository");
        await RunGitAsync(_temporaryDirectory!, "init", "--quiet", "--initial-branch=main", "--", repositoryPath);
        const string fileName = "revert lines.txt";
        var filePath = Path.Combine(repositoryPath, fileName);
        var baselineLines = Enumerable.Range(1, 12).Select(static line => $"line {line}").ToArray();
        File.WriteAllText(filePath, string.Join('\n', baselineLines) + "\n");
        await RunGitAsync(repositoryPath, "add", "--", fileName);
        await RunGitAsync(
            repositoryPath,
            "-c",
            "user.name=GitSail Tests",
            "-c",
            "user.email=gitsail@example.invalid",
            "commit",
            "--quiet",
            "-m",
            "baseline");
        var changedLines = baselineLines.ToArray();
        changedLines[3] = "revert this replacement";
        changedLines[6] = "retain this replacement";
        var changedContent = string.Join('\n', changedLines) + "\n";
        File.WriteAllText(filePath, changedContent);
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var environmentFactory = TestProcessEnvironment.CreateGitFactory(_temporaryDirectory!);
        var rawDiffService = new RawDiffService(_installation!, _runner!, environmentFactory);
        using var workTreeDiff = await rawDiffService.CaptureAsync(
            workingDirectory,
            RawDiffTarget.WorkTree,
            new OperationGeneration(1),
            TestContext.Current!.CancellationToken);
        var workTreeFile = workTreeDiff.Index.Find(CreatePath(fileName));
        Assert.IsNotNull(workTreeFile);
        var selectedLines = workTreeFile.PatchIndex.Hunks
            .SelectMany(static hunk => hunk.Lines)
            .Where(static line => line.Kind is RawPatchLineKind.Deletion or RawPatchLineKind.Addition)
            .Take(2)
            .Select(static line => line.LineNumber)
            .ToHashSet();
        var revertPatch = await workTreeDiff.ReadSelectedLinesPatchAsync(
            workTreeFile,
            selectedLines,
            RawPatchSelectionSide.PreserveNewSide,
            TestContext.Current.CancellationToken);
        using var coordinator = new RepositoryMutationCoordinator();
        var patchService = new RepositoryPatchService(
            _installation!,
            _runner!,
            environmentFactory,
            coordinator);

        _ = await patchService.RevertAsync(
            workingDirectory,
            revertPatch,
            TestContext.Current.CancellationToken);
        var revertedLines = changedLines.ToArray();
        revertedLines[3] = baselineLines[3];
        Assert.AreEqual(string.Join('\n', revertedLines) + "\n", File.ReadAllText(filePath));

        _ = await patchService.UndoRevertAsync(
            workingDirectory,
            revertPatch,
            TestContext.Current.CancellationToken);

        Assert.AreEqual(changedContent, File.ReadAllText(filePath));
    }

    /// <summary>
    /// Verifies revert preflight rejects stale patch context without changing newer worktree content.
    /// </summary>
    [TestMethod]
    public async Task RevertAsync_AfterWorkTreeChanged_RejectsBeforeMutation()
    {
        var repositoryPath = Path.Combine(_temporaryDirectory!, "stale-revert-repository");
        await RunGitAsync(_temporaryDirectory!, "init", "--quiet", "--initial-branch=main", "--", repositoryPath);
        const string fileName = "stale.txt";
        var filePath = Path.Combine(repositoryPath, fileName);
        File.WriteAllText(filePath, "baseline\ncontext\n");
        await RunGitAsync(repositoryPath, "add", "--", fileName);
        await RunGitAsync(
            repositoryPath,
            "-c",
            "user.name=GitSail Tests",
            "-c",
            "user.email=gitsail@example.invalid",
            "commit",
            "--quiet",
            "-m",
            "baseline");
        File.WriteAllText(filePath, "changed\ncontext\n");
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var environmentFactory = TestProcessEnvironment.CreateGitFactory(_temporaryDirectory!);
        var rawDiffService = new RawDiffService(_installation!, _runner!, environmentFactory);
        using var workTreeDiff = await rawDiffService.CaptureAsync(
            workingDirectory,
            RawDiffTarget.WorkTree,
            new OperationGeneration(1),
            TestContext.Current!.CancellationToken);
        var workTreeFile = workTreeDiff.Index.Find(CreatePath(fileName));
        Assert.IsNotNull(workTreeFile);
        var patch = await workTreeDiff.ReadFileAsync(workTreeFile, TestContext.Current.CancellationToken);
        const string newerContent = "newer concurrent content\ncontext\n";
        File.WriteAllText(filePath, newerContent);
        using var coordinator = new RepositoryMutationCoordinator();
        var patchService = new RepositoryPatchService(
            _installation!,
            _runner!,
            environmentFactory,
            coordinator);

        _ = await Assert.ThrowsExactlyAsync<GitCommandException>(() => patchService.RevertAsync(
            workingDirectory,
            patch,
            TestContext.Current.CancellationToken));

        Assert.AreEqual(newerContent, File.ReadAllText(filePath));
    }

    /// <summary>
    /// Verifies complete binary patch revert and undo round-trip exact bytes without text presentation.
    /// </summary>
    [TestMethod]
    public async Task RevertAndUndoRevertAsync_WithBinaryPatch_RoundTripsExactBytes()
    {
        var repositoryPath = Path.Combine(_temporaryDirectory!, "binary-revert-repository");
        await RunGitAsync(_temporaryDirectory!, "init", "--quiet", "--initial-branch=main", "--", repositoryPath);
        const string fileName = "binary.dat";
        var filePath = Path.Combine(repositoryPath, fileName);
        byte[] baseline = [0, 1, 2, 3, 4, 0, 255];
        byte[] changed = [0, 1, 9, 8, 7, 0, 255, 6];
        File.WriteAllBytes(filePath, baseline);
        await RunGitAsync(repositoryPath, "add", "--", fileName);
        await RunGitAsync(
            repositoryPath,
            "-c",
            "user.name=GitSail Tests",
            "-c",
            "user.email=gitsail@example.invalid",
            "commit",
            "--quiet",
            "-m",
            "baseline");
        File.WriteAllBytes(filePath, changed);
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var environmentFactory = TestProcessEnvironment.CreateGitFactory(_temporaryDirectory!);
        var rawDiffService = new RawDiffService(_installation!, _runner!, environmentFactory);
        using var workTreeDiff = await rawDiffService.CaptureAsync(
            workingDirectory,
            RawDiffTarget.WorkTree,
            new OperationGeneration(1),
            TestContext.Current!.CancellationToken);
        var workTreeFile = workTreeDiff.Index.Find(CreatePath(fileName));
        Assert.IsNotNull(workTreeFile);
        Assert.IsTrue(workTreeFile.IsBinary);
        var patch = await workTreeDiff.ReadFileAsync(workTreeFile, TestContext.Current.CancellationToken);
        using var coordinator = new RepositoryMutationCoordinator();
        var patchService = new RepositoryPatchService(
            _installation!,
            _runner!,
            environmentFactory,
            coordinator);

        _ = await patchService.RevertAsync(
            workingDirectory,
            patch,
            TestContext.Current.CancellationToken);
        CollectionAssert.AreEqual(baseline, File.ReadAllBytes(filePath));

        _ = await patchService.UndoRevertAsync(
            workingDirectory,
            patch,
            TestContext.Current.CancellationToken);

        CollectionAssert.AreEqual(changed, File.ReadAllBytes(filePath));
    }

    private async Task RunGitAsync(string workingDirectory, params string[] arguments)
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
    }

    private static async Task<byte[]> CaptureSingleFilePatchAsync(
        RawDiffService service,
        CanonicalDirectory workingDirectory,
        RawDiffTarget target,
        OperationGeneration generation,
        string fileName)
    {
        using var document = await service.CaptureAsync(
            workingDirectory,
            target,
            generation,
            TestContext.Current!.CancellationToken);
        var file = document.Index.Find(CreatePath(fileName));
        Assert.IsNotNull(file);
        return await document.ReadFileAsync(file, TestContext.Current.CancellationToken);
    }

    private static GitPath CreatePath(string path)
        => OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath(path)
            : GitPath.FromUnixBytes(Encoding.UTF8.GetBytes(path));
}
