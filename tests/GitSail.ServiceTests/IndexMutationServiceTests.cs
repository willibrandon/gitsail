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
        var mutationService = new IndexMutationService(
            _installation!,
            _runner!,
            environmentFactory,
            coordinator);

        _ = await mutationService.StagePatchAsync(
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

        _ = await mutationService.UnstagePatchAsync(
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
