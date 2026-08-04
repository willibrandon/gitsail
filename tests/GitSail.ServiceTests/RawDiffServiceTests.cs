using GitSail.Domain;
using GitSail.Git.Execution;
using System.Text;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies exact raw worktree and index patch capture against isolated Git repositories.
/// </summary>
[TestClass]
public sealed class RawDiffServiceTests
{
    private string? _temporaryDirectory;
    private GitInstallation? _installation;
    private ChildProcessRunner? _runner;

    /// <summary>
    /// Creates an isolated home and resolves Git for each raw-diff test.
    /// </summary>
    [TestInitialize]
    public async Task InitializeAsync()
    {
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"gitsail-diff-{Guid.NewGuid():N}");
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
    /// Verifies separate worktree and index patches plus rename-aware exact path metadata.
    /// </summary>
    [TestMethod]
    public async Task CaptureAsync_WithMixedChanges_SeparatesTargetsAndIndexesRename()
    {
        var repositoryPath = Path.Combine(_temporaryDirectory!, "repository");
        await RunGitAsync(_temporaryDirectory!, "init", "--quiet", "--initial-branch=main", "--", repositoryPath);
        File.WriteAllText(Path.Combine(repositoryPath, "work tree.txt"), "old work\n");
        File.WriteAllText(Path.Combine(repositoryPath, "staged.txt"), "old staged\n");
        File.WriteAllText(Path.Combine(repositoryPath, "old name.txt"), "rename content\n");
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
        File.WriteAllText(Path.Combine(repositoryPath, "work tree.txt"), "new work\n");
        File.WriteAllText(Path.Combine(repositoryPath, "staged.txt"), "new staged\n");
        await RunGitAsync(repositoryPath, "add", "--", "staged.txt");
        await RunGitAsync(repositoryPath, "mv", "--", "old name.txt", "renamed name.txt");
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var service = new RawDiffService(
            _installation!,
            _runner!,
            TestProcessEnvironment.CreateGitFactory(_temporaryDirectory!));

        using var workTree = await service.CaptureAsync(
            workingDirectory,
            RawDiffTarget.WorkTree,
            new OperationGeneration(7),
            TestContext.Current!.CancellationToken);
        using var index = await service.CaptureAsync(
            workingDirectory,
            RawDiffTarget.Index,
            new OperationGeneration(8),
            TestContext.Current.CancellationToken);

        Assert.AreEqual(7L, workTree.Index.Generation.Value);
        Assert.HasCount(1, workTree.Index.Files);
        var workFile = workTree.Index.Find(CreatePath("work tree.txt"));
        Assert.IsNotNull(workFile);
        Assert.IsTrue(workFile.HasHunks);
        var workPatch = Encoding.UTF8.GetString(await workTree.ReadFileAsync(
            workFile,
            TestContext.Current.CancellationToken));
        StringAssert.Contains(workPatch, "-old work\n+new work");

        Assert.AreEqual(8L, index.Index.Generation.Value);
        Assert.HasCount(2, index.Index.Files);
        var stagedFile = index.Index.Find(CreatePath("staged.txt"));
        Assert.IsNotNull(stagedFile);
        Assert.IsTrue(stagedFile.HasHunks);
        var stagedPatch = Encoding.UTF8.GetString(await index.ReadFileAsync(
            stagedFile,
            TestContext.Current.CancellationToken));
        StringAssert.Contains(stagedPatch, "-old staged\n+new staged");
        var rename = index.Index.Find(CreatePath("renamed name.txt"));
        Assert.IsNotNull(rename);
        AssertPathEquals("old name.txt", rename.OldPath);
        AssertPathEquals("renamed name.txt", rename.NewPath);
        Assert.IsFalse(rename.HasHunks);
    }

    /// <summary>
    /// Verifies explicit unified context controls hunk coalescing without changing raw file identity.
    /// </summary>
    [TestMethod]
    public async Task CaptureAsync_WithExplicitContext_ControlsHunkCoalescing()
    {
        var repositoryPath = Path.Combine(_temporaryDirectory!, "context-repository");
        await RunGitAsync(_temporaryDirectory!, "init", "--quiet", "--initial-branch=main", "--", repositoryPath);
        const string fileName = "context.txt";
        var filePath = Path.Combine(repositoryPath, fileName);
        var baseline = Enumerable.Range(1, 20).Select(static line => $"line {line}").ToArray();
        File.WriteAllText(filePath, string.Join('\n', baseline) + "\n");
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
        var changed = baseline.ToArray();
        changed[4] = "changed five";
        changed[11] = "changed twelve";
        File.WriteAllText(filePath, string.Join('\n', changed) + "\n");
        var service = new RawDiffService(
            _installation!,
            _runner!,
            TestProcessEnvironment.CreateGitFactory(_temporaryDirectory!));
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);

        using var noContext = await service.CaptureAsync(
            workingDirectory,
            RawDiffTarget.WorkTree,
            new OperationGeneration(1),
            contextLines: 0,
            TestContext.Current!.CancellationToken);
        using var threeLines = await service.CaptureAsync(
            workingDirectory,
            RawDiffTarget.WorkTree,
            new OperationGeneration(2),
            contextLines: 3,
            TestContext.Current.CancellationToken);

        var noContextFile = noContext.Index.Find(CreatePath(fileName));
        var threeLineFile = threeLines.Index.Find(CreatePath(fileName));
        Assert.IsNotNull(noContextFile);
        Assert.IsNotNull(threeLineFile);
        Assert.HasCount(2, noContextFile.PatchIndex.Hunks);
        Assert.HasCount(1, threeLineFile.PatchIndex.Hunks);
        Assert.AreEqual(noContextFile.NewPath, threeLineFile.NewPath);
    }

    /// <summary>
    /// Verifies exact commit pairs, commit-to-worktree, commit-to-index, and native path filtering.
    /// </summary>
    [TestMethod]
    public async Task CaptureComparisonAsync_WithRevisionsAndPathspecs_CapturesRequestedSides()
    {
        var repositoryPath = Path.Combine(_temporaryDirectory!, "comparison-repository");
        await RunGitAsync(_temporaryDirectory!, "init", "--quiet", "--initial-branch=main", "--", repositoryPath);
        const string selectedName = "selected file.txt";
        const string otherName = "other.txt";
        File.WriteAllText(Path.Combine(repositoryPath, selectedName), "baseline selected\n");
        File.WriteAllText(Path.Combine(repositoryPath, otherName), "baseline other\n");
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
        var baseline = await ResolveHeadAsync(repositoryPath);
        File.WriteAllText(Path.Combine(repositoryPath, selectedName), "committed selected\n");
        File.WriteAllText(Path.Combine(repositoryPath, otherName), "committed other\n");
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
            "second");
        var second = await ResolveHeadAsync(repositoryPath);
        var pathspec = CreatePath(selectedName);
        var service = new RawDiffService(
            _installation!,
            _runner!,
            TestProcessEnvironment.CreateGitFactory(_temporaryDirectory!));
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);

        using var commits = await service.CaptureComparisonAsync(
            workingDirectory,
            DiffRequest.CommitToCommit(baseline, second, [pathspec]),
            new OperationGeneration(3),
            contextLines: 3,
            TestContext.Current!.CancellationToken);

        Assert.HasCount(1, commits.Index.Files);
        var commitFile = commits.Index.Find(pathspec);
        Assert.IsNotNull(commitFile);
        var commitPatch = Encoding.UTF8.GetString(await commits.ReadFileAsync(
            commitFile,
            TestContext.Current.CancellationToken));
        StringAssert.Contains(commitPatch, "-baseline selected\n+committed selected");
        Assert.IsFalse(commitPatch.Contains(otherName, StringComparison.Ordinal));

        File.WriteAllText(Path.Combine(repositoryPath, selectedName), "worktree selected\n");
        using var worktree = await service.CaptureComparisonAsync(
            workingDirectory,
            DiffRequest.CommitToWorkTree(second, [pathspec]),
            new OperationGeneration(4),
            contextLines: 3,
            TestContext.Current.CancellationToken);
        var worktreeFile = worktree.Index.Find(pathspec);
        Assert.IsNotNull(worktreeFile);
        var worktreePatch = Encoding.UTF8.GetString(await worktree.ReadFileAsync(
            worktreeFile,
            TestContext.Current.CancellationToken));
        StringAssert.Contains(worktreePatch, "-committed selected\n+worktree selected");

        await RunGitAsync(repositoryPath, "add", "--", selectedName);
        using var index = await service.CaptureComparisonAsync(
            workingDirectory,
            DiffRequest.CommitToIndex(second, [pathspec]),
            new OperationGeneration(5),
            contextLines: 3,
            TestContext.Current.CancellationToken);
        var indexFile = index.Index.Find(pathspec);
        Assert.IsNotNull(indexFile);
        var indexPatch = Encoding.UTF8.GetString(await index.ReadFileAsync(
            indexFile,
            TestContext.Current.CancellationToken));
        StringAssert.Contains(indexPatch, "-committed selected\n+worktree selected");
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
            OutputPolicy.Create(1024 * 1024, 1024 * 1024));

        var result = await _runner!.RunAsync(invocation, TestContext.Current!.CancellationToken);

        Assert.AreEqual(0, result.ExitCode, Encoding.UTF8.GetString(result.StandardError.Span));
        return Encoding.UTF8.GetString(result.StandardOutput.Span);
    }

    private async Task<ObjectId> ResolveHeadAsync(string repositoryPath)
    {
        var output = (await RunGitForOutputAsync(repositoryPath, "rev-parse", "HEAD")).Trim();
        Assert.IsTrue(ObjectId.TryParseHex(Encoding.ASCII.GetBytes(output), out var objectId));
        return objectId!;
    }

    private static GitPath CreatePath(string path)
        => OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath(path)
            : GitPath.FromUnixBytes(Encoding.UTF8.GetBytes(path));

    private static void AssertPathEquals(string expected, GitPath actual)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.AreEqual(expected, actual.GetWindowsPath());
            return;
        }

        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(expected), actual.GetUnixBytes().ToArray());
    }
}
