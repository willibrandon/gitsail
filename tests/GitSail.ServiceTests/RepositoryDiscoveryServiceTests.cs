using GitSail.Domain;
using GitSail.Git.Execution;
using System.Text;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies repository discovery against isolated real Git repositories.
/// </summary>
[TestClass]
public sealed class RepositoryDiscoveryServiceTests
{
    private string? _temporaryDirectory;
    private GitInstallation? _installation;
    private ChildProcessRunner? _runner;

    /// <summary>
    /// Creates an isolated home and resolves Git for each repository-discovery test.
    /// </summary>
    [TestInitialize]
    public async Task InitializeAsync()
    {
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"gitsail-discovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);
        _runner = new ChildProcessRunner();
        var resolver = new ExecutableResolver(new RuntimeProcessEnvironment());
        var service = new GitVersionService(resolver, _runner);
        _installation = await service.GetAsync(
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
    /// Verifies worktree, Git directory, common directory, prefix, and object-format discovery.
    /// </summary>
    [TestMethod]
    public async Task DiscoverAsync_InsideWorktree_ReturnsCanonicalRepositoryLocations()
    {
        var repositoryPath = Path.Combine(_temporaryDirectory!, "repository");
        await InitializeRepositoryAsync(repositoryPath, bare: false);
        var nestedPath = Path.Combine(repositoryPath, "nested");
        Directory.CreateDirectory(nestedPath);
        var service = new RepositoryDiscoveryService(
            _installation!,
            _runner!,
            TestProcessEnvironment.CreateGitFactory(_temporaryDirectory!));

        var location = await service.DiscoverAsync(
            CanonicalDirectory.Create(nestedPath),
            TestContext.Current!.CancellationToken);

        Assert.IsFalse(location.IsBare);
        Assert.IsNotNull(location.WorkTree);
        Assert.AreEqual(RepositoryObjectFormat.Sha1, location.ObjectFormat);
        StringAssert.EndsWith(GetDisplayText(location.GitDirectory), "/repository/.git");
        Assert.AreEqual(GetDisplayText(location.GitDirectory), GetDisplayText(location.CommonDirectory));
        StringAssert.EndsWith(GetDisplayText(location.WorkTree), "/repository");
        Assert.AreEqual("nested/", GetDisplayText(location.Prefix));
    }

    /// <summary>
    /// Verifies that a bare repository has no worktree while retaining canonical storage locations.
    /// </summary>
    [TestMethod]
    public async Task DiscoverAsync_InsideBareRepository_ReturnsNoWorkTree()
    {
        var repositoryPath = Path.Combine(_temporaryDirectory!, "bare.git");
        await InitializeRepositoryAsync(repositoryPath, bare: true);
        var service = new RepositoryDiscoveryService(
            _installation!,
            _runner!,
            TestProcessEnvironment.CreateGitFactory(_temporaryDirectory!));

        var location = await service.DiscoverAsync(
            CanonicalDirectory.Create(repositoryPath),
            TestContext.Current!.CancellationToken);

        Assert.IsTrue(location.IsBare);
        Assert.IsNull(location.WorkTree);
        Assert.IsNull(location.Prefix);
        Assert.AreEqual(GetDisplayText(location.GitDirectory), GetDisplayText(location.CommonDirectory));
    }

    /// <summary>
    /// Verifies that discovery outside a repository reports Git's nonzero result.
    /// </summary>
    [TestMethod]
    public async Task DiscoverAsync_OutsideRepository_ThrowsGitCommandException()
    {
        var service = new RepositoryDiscoveryService(
            _installation!,
            _runner!,
            TestProcessEnvironment.CreateGitFactory(_temporaryDirectory!));

        var exception = await Assert.ThrowsExactlyAsync<GitCommandException>(() => service.DiscoverAsync(
            CanonicalDirectory.Create(_temporaryDirectory!),
            TestContext.Current!.CancellationToken));

        Assert.AreNotEqual(0, exception.ExitCode);
    }

    /// <summary>
    /// Verifies explicit startup Git directory and worktree overrides select the expected repository.
    /// </summary>
    [TestMethod]
    public async Task DiscoverAsync_WithStartupRepositoryOverrides_HonorsSelectedRepository()
    {
        var repositoryPath = Path.Combine(_temporaryDirectory!, "selected-repository");
        await InitializeRepositoryAsync(repositoryPath, bare: false);
        var launchPath = Path.Combine(_temporaryDirectory!, "outside");
        Directory.CreateDirectory(launchPath);
        var environment = new TestProcessEnvironment(new Dictionary<string, string?>
        {
            ["HOME"] = _temporaryDirectory,
            ["USERPROFILE"] = _temporaryDirectory,
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_DIR"] = Path.Combine(repositoryPath, ".git"),
            ["GIT_WORK_TREE"] = repositoryPath,
        });
        var service = new RepositoryDiscoveryService(
            _installation!,
            _runner!,
            new GitChildEnvironmentFactory(environment));

        var location = await service.DiscoverAsync(
            CanonicalDirectory.Create(launchPath),
            TestContext.Current!.CancellationToken);

        Assert.IsFalse(location.IsBare);
        StringAssert.EndsWith(GetDisplayText(location.WorkTree), "/selected-repository");
    }

    private async Task InitializeRepositoryAsync(string path, bool bare)
    {
        var arguments = new List<ProcessArgument>
        {
            ProcessArgument.Literal("init"),
            ProcessArgument.Literal("--quiet"),
            ProcessArgument.Literal("--initial-branch=main"),
        };
        if (bare)
        {
            arguments.Add(ProcessArgument.Literal("--bare"));
        }

        arguments.Add(ProcessArgument.Literal("--"));
        arguments.Add(ProcessArgument.Literal(path));
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
            [.. arguments],
            CanonicalDirectory.Create(_temporaryDirectory!),
            environment,
            StandardInputSource.Empty(),
            OutputPolicy.Create(64 * 1024, 64 * 1024));

        var result = await _runner!.RunAsync(invocation, TestContext.Current!.CancellationToken);

        Assert.AreEqual(0, result.ExitCode, Encoding.UTF8.GetString(result.StandardError.Span));
    }

    private static string GetDisplayText(GitPath? path)
        => (path ?? throw new AssertFailedException("Expected a discovered Git path."))
            .DisplayText
            .Replace('\\', '/');
}
