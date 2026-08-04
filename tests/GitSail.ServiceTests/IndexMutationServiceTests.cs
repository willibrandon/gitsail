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
        var repository = await new RepositoryDiscoveryService(_installation!, _runner!).DiscoverAsync(
            workingDirectory,
            TestContext.Current!.CancellationToken);
        var statusService = new RepositoryStatusService(
            _installation!,
            _runner!,
            new PorcelainV2StatusParser());
        var initial = await statusService.ScanAsync(
            repository,
            workingDirectory,
            new OperationGeneration(1),
            TestContext.Current.CancellationToken);
        var paths = initial.Entries.Select(static entry => entry.Path).ToArray();
        using var coordinator = new RepositoryMutationCoordinator();
        var service = new IndexMutationService(_installation!, _runner!, coordinator);

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
