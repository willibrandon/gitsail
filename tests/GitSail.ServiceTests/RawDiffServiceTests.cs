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
            Directory.Delete(_temporaryDirectory, recursive: true);
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
