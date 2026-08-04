using GitSail.Domain;
using GitSail.Git.Execution;
using System.Text;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies allowlisted state-path resolution against ordinary and linked worktrees.
/// </summary>
[TestClass]
public sealed class RepositoryStatePathServiceTests
{
    private string? _temporaryDirectory;
    private GitInstallation? _installation;
    private ChildProcessRunner? _runner;

    /// <summary>
    /// Creates an isolated home and resolves Git for each state-path test.
    /// </summary>
    [TestInitialize]
    public async Task InitializeAsync()
    {
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"gitsail-state-path-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);
        _runner = new ChildProcessRunner();
        _installation = await new GitVersionService(
            new ExecutableResolver(new RuntimeProcessEnvironment()),
            _runner).GetAsync(
            CanonicalDirectory.Create(_temporaryDirectory),
            TestContext.Current!.CancellationToken);
    }

    /// <summary>
    /// Removes the isolated repositories and home after each test.
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
    /// Verifies every closed-set state file resolves beneath the ordinary repository Git directory.
    /// </summary>
    [TestMethod]
    public async Task ResolveAsync_ForEveryAllowlistedFile_ReturnsAbsoluteGitPath()
    {
        var repositoryPath = Path.Combine(_temporaryDirectory!, "repository");
        await InitializeRepositoryWithCommitAsync(repositoryPath);
        var service = CreateService();
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var repository = await new RepositoryDiscoveryService(
            _installation!,
            _runner!,
            TestProcessEnvironment.CreateGitFactory(_temporaryDirectory!)).DiscoverAsync(
            workingDirectory,
            TestContext.Current!.CancellationToken);
        var expectedPrefix = GetNativeText(repository.GitDirectory).Replace('\\', '/') + "/";

        foreach (var stateFile in Enum.GetValues<RepositoryStateFile>())
        {
            var path = await service.ResolveAsync(
                workingDirectory,
                stateFile,
                TestContext.Current!.CancellationToken);
            var displayPath = GetNativeText(path).Replace('\\', '/');

            Assert.IsTrue(displayPath.StartsWith(
                expectedPrefix,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal),
                $"{stateFile} resolved outside the expected Git directory: {displayPath}");
            Assert.IsTrue(Path.IsPathFullyQualified(GetNativeText(path)));
        }
    }

    /// <summary>
    /// Verifies a linked worktree resolves per-worktree state away from the common Git directory root.
    /// </summary>
    [TestMethod]
    public async Task ResolveAsync_FromLinkedWorktree_ReturnsPerWorktreePath()
    {
        var repositoryPath = Path.Combine(_temporaryDirectory!, "repository");
        var linkedPath = Path.Combine(_temporaryDirectory!, "linked");
        await InitializeRepositoryWithCommitAsync(repositoryPath);
        await RunGitAsync(repositoryPath, "worktree", "add", "--quiet", "--detach", "--", linkedPath);
        var service = CreateService();

        var path = await service.ResolveAsync(
            CanonicalDirectory.Create(linkedPath),
            RepositoryStateFile.EditMessage,
            TestContext.Current!.CancellationToken);
        var displayPath = GetNativeText(path).Replace('\\', '/');

        StringAssert.Contains(displayPath, "/.git/worktrees/");
        StringAssert.EndsWith(displayPath, "/GITGUI_EDITMSG");
    }

    private RepositoryStatePathService CreateService()
        => new(
            _installation!,
            _runner!,
            TestProcessEnvironment.CreateGitFactory(_temporaryDirectory!));

    private async Task InitializeRepositoryWithCommitAsync(string repositoryPath)
    {
        await RunGitAsync(
            _temporaryDirectory!,
            "init",
            "--quiet",
            "--initial-branch=main",
            "--",
            repositoryPath);
        File.WriteAllText(Path.Combine(repositoryPath, "tracked.txt"), "tracked\n");
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

    private static string GetNativeText(GitPath path)
        => path.Kind == NativePathKind.WindowsUtf16
            ? path.GetWindowsPath()
            : Encoding.UTF8.GetString(path.GetUnixBytes());
}
