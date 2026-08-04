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
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    /// <summary>
    /// Verifies staged, unstaged, and untracked status from one generation-stamped scan.
    /// </summary>
    [TestMethod]
    public async Task ScanAsync_WithMixedWorktree_ReturnsStructuredEntries()
    {
        var repositoryPath = Path.Combine(_temporaryDirectory!, "repository");
        await RunGitAsync(_temporaryDirectory!, "init", "--quiet", "--initial-branch=main", "--", repositoryPath);
        var trackedPath = Path.Combine(repositoryPath, "tracked.txt");
        File.WriteAllText(trackedPath, "staged\n");
        await RunGitAsync(repositoryPath, "add", "--", "tracked.txt");
        File.AppendAllText(trackedPath, "unstaged\n");
        File.WriteAllText(Path.Combine(repositoryPath, "untracked.txt"), "new\n");
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var repository = await new RepositoryDiscoveryService(_installation!, _runner!).DiscoverAsync(
            workingDirectory,
            TestContext.Current!.CancellationToken);
        var service = new RepositoryStatusService(
            _installation!,
            _runner!,
            new PorcelainV2StatusParser());

        var snapshot = await service.ScanAsync(
            repository,
            workingDirectory,
            new OperationGeneration(12),
            TestContext.Current!.CancellationToken);

        Assert.AreEqual(12L, snapshot.Generation.Value);
        Assert.IsNull(snapshot.HeadObjectId);
        Assert.AreEqual("main", snapshot.HeadName?.DisplayText);
        Assert.HasCount(2, snapshot.Entries);
        var tracked = snapshot.Entries.Single(static entry => entry.Path.DisplayText == "tracked.txt");
        Assert.AreEqual(GitFileStatus.Added, tracked.IndexStatus);
        Assert.AreEqual(GitFileStatus.Modified, tracked.WorkTreeStatus);
        var untracked = snapshot.Entries.Single(static entry => entry.Path.DisplayText == "untracked.txt");
        Assert.AreEqual(RepositoryStatusEntryKind.Untracked, untracked.Kind);
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
}
