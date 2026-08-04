using GitSail.Domain;
using GitSail.Git.Execution;
using System.Text;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies stable remote capture and revalidated Git-owned transport against real repositories.
/// </summary>
[TestClass]
public sealed class RemoteServiceTests
{
    private string? _temporaryDirectory;
    private GitInstallation? _installation;
    private ChildProcessRunner? _runner;
    private RepositoryMutationCoordinator? _coordinator;
    private GitChildEnvironmentFactory? _environmentFactory;

    /// <summary>
    /// Creates an isolated home and resolves Git for each remote-service test.
    /// </summary>
    [TestInitialize]
    public async Task InitializeAsync()
    {
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"gitsail-remotes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);
        _runner = new ChildProcessRunner();
        _coordinator = new RepositoryMutationCoordinator();
        _environmentFactory = TestProcessEnvironment.CreateGitFactory(_temporaryDirectory);
        _installation = await new GitVersionService(
            new ExecutableResolver(new RuntimeProcessEnvironment()),
            _runner).GetAsync(
            CanonicalDirectory.Create(_temporaryDirectory),
            TestContext.Current!.CancellationToken);
    }

    /// <summary>
    /// Removes isolated repositories and the mutation coordinator after each test.
    /// </summary>
    [TestCleanup]
    public void Cleanup()
    {
        _coordinator?.Dispose();
        if (_temporaryDirectory is not null && Directory.Exists(_temporaryDirectory))
        {
            TestDirectory.Delete(_temporaryDirectory);
        }
    }

    /// <summary>
    /// Verifies exact names, multiple URLs, push fallback, and display redaction survive stable capture.
    /// </summary>
    [TestMethod]
    public async Task CaptureAsync_WithMultipleAndHostileConfiguredValues_ReturnsExactStableCatalog()
    {
        var repositoryPath = await CreateRepositoryAsync("catalog");
        await RunGitAsync(
            repositoryPath,
            "remote",
            "add",
            "--",
            "-dash",
            "https://person:password@example.invalid/one?token=secret");
        await RunGitAsync(
            repositoryPath,
            "config",
            "--add",
            "remote.-dash.url",
            "ssh://example.invalid/two");
        await RunGitAsync(
            repositoryPath,
            "config",
            "--add",
            "remote.-dash.pushurl",
            "ssh://push.example.invalid/repository");
        var service = CreateService();

        var catalog = await service.CaptureAsync(
            CanonicalDirectory.Create(repositoryPath),
            TestContext.Current!.CancellationToken);

        Assert.HasCount(1, catalog.Remotes);
        var remote = catalog.Remotes[0];
        Assert.AreEqual("-dash", remote.Name.DisplayText);
        Assert.HasCount(2, remote.FetchUrls);
        Assert.HasCount(1, remote.PushUrls);
        Assert.AreEqual(
            "https://example.invalid/one?<redacted>",
            remote.FetchUrls[0].RedactedDisplayText);
        Assert.AreEqual(
            "ssh://push.example.invalid/repository",
            remote.PushUrls[0].RedactedDisplayText);
    }

    /// <summary>
    /// Verifies documented empty URL values clear inherited lists before later effective values.
    /// </summary>
    [TestMethod]
    public async Task CaptureAsync_WithEmptyUrlReset_ReturnsOnlyEffectiveValues()
    {
        var repositoryPath = await CreateRepositoryAsync("url-reset");
        await RunGitAsync(repositoryPath, "remote", "add", "origin", "fetch-one");
        await RunGitAsync(repositoryPath, "config", "--add", "remote.origin.url", "fetch-two");
        await RunGitAsync(repositoryPath, "config", "--add", "remote.origin.url", string.Empty);
        await RunGitAsync(repositoryPath, "config", "--add", "remote.origin.url", "fetch-final");
        await RunGitAsync(repositoryPath, "config", "--add", "remote.origin.pushurl", "push-one");
        await RunGitAsync(repositoryPath, "config", "--add", "remote.origin.pushurl", string.Empty);
        await RunGitAsync(repositoryPath, "config", "--add", "remote.origin.pushurl", "push-final");

        var catalog = await CreateService().CaptureAsync(
            CanonicalDirectory.Create(repositoryPath),
            TestContext.Current!.CancellationToken);

        var remote = catalog.Remotes.Single();
        Assert.HasCount(1, remote.FetchUrls);
        Assert.AreEqual("fetch-final", remote.FetchUrls[0].RedactedDisplayText);
        Assert.HasCount(1, remote.PushUrls);
        Assert.AreEqual("push-final", remote.PushUrls[0].RedactedDisplayText);
    }

    /// <summary>
    /// Verifies add, typed fetch, and remove use the exact selected remote and update Git-owned refs.
    /// </summary>
    [TestMethod]
    public async Task AddFetchAndRemoveAsync_WithLocalRemote_RoundTripsExactSelection()
    {
        var repositoryPath = await CreateRepositoryAsync("working");
        var remotePath = Path.Combine(_temporaryDirectory!, "upstream.git");
        await RunGitAsync(_temporaryDirectory!, "init", "--quiet", "--bare", "--", remotePath);
        await RunGitAsync(repositoryPath, "push", "--quiet", remotePath, "main:main");
        await RunGitAsync(repositoryPath, "switch", "--quiet", "--create", "feature");
        File.AppendAllText(Path.Combine(repositoryPath, "tracked.txt"), "feature\n");
        await RunGitAsync(repositoryPath, "add", "--", "tracked.txt");
        await CommitAsync(repositoryPath, "feature");
        await RunGitAsync(repositoryPath, "push", "--quiet", remotePath, "feature:feature");
        await RunGitAsync(repositoryPath, "switch", "--quiet", "main");
        var service = CreateService();
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var emptyCatalog = await service.CaptureAsync(
            workingDirectory,
            TestContext.Current!.CancellationToken);
        var name = await service.ValidateNameAsync(
            workingDirectory,
            "-dash",
            TestContext.Current.CancellationToken);

        _ = await service.AddAsync(
            workingDirectory,
            emptyCatalog,
            name,
            RemoteUrl.FromText(remotePath),
            TestContext.Current.CancellationToken);
        var addedCatalog = await service.CaptureAsync(
            workingDirectory,
            TestContext.Current.CancellationToken);
        var selectedRemote = addedCatalog.Find(name);
        Assert.IsNotNull(selectedRemote);

        _ = await service.FetchAsync(
            workingDirectory,
            addedCatalog,
            selectedRemote,
            new FetchOptions(GitOptionOverride.Enabled, FetchTagMode.None),
            TestContext.Current.CancellationToken);

        var fetched = await RunGitForOutputAsync(
            repositoryPath,
            "rev-parse",
            "--verify",
            "refs/remotes/-dash/feature");
        Assert.HasCount(40, fetched.Trim());
        var currentCatalog = await service.CaptureAsync(
            workingDirectory,
            TestContext.Current.CancellationToken);
        _ = await service.RemoveAsync(
            workingDirectory,
            currentCatalog,
            currentCatalog.Find(name)!,
            TestContext.Current.CancellationToken);
        Assert.IsTrue((await service.CaptureAsync(
            workingDirectory,
            TestContext.Current.CancellationToken)).Remotes.IsEmpty);
    }

    /// <summary>
    /// Verifies prune confirmation removes only refs from the exact unchanged remote configuration.
    /// </summary>
    [TestMethod]
    public async Task PrepareAndPruneAsync_AfterRemoteBranchDeleted_RemovesStaleTrackingRef()
    {
        var setup = await CreateFetchedRemoteAsync("prune");
        await RunGitAsync(setup.RepositoryPath, "push", "--quiet", setup.RemotePath, ":feature");
        var service = CreateService();
        var workingDirectory = CanonicalDirectory.Create(setup.RepositoryPath);
        var catalog = await service.CaptureAsync(
            workingDirectory,
            TestContext.Current!.CancellationToken);
        var origin = catalog.Remotes.Single();

        var plan = await service.PreparePruneAsync(
            workingDirectory,
            catalog,
            origin,
            TestContext.Current.CancellationToken);
        var stalePlan = new RemotePrunePlan(
            plan.Catalog,
            plan.Remote,
            new GitOperationResult("different preview\n"u8.ToArray(), ReadOnlyMemory<byte>.Empty));
        _ = await Assert.ThrowsExactlyAsync<RepositoryPreconditionException>(() => service.PruneAsync(
            workingDirectory,
            stalePlan,
            TestContext.Current.CancellationToken));
        _ = await service.PruneAsync(
            workingDirectory,
            plan,
            TestContext.Current.CancellationToken);

        var missing = await RunGitAsync(
            setup.RepositoryPath,
            ["rev-parse", "--verify", "--quiet", "refs/remotes/origin/feature"],
            expectSuccess: false);
        Assert.AreEqual(1, missing.ExitCode);
    }

    /// <summary>
    /// Verifies a concurrent URL change rejects a fetch before transport begins.
    /// </summary>
    [TestMethod]
    public async Task FetchAsync_AfterRemoteUrlChanged_RejectsStaleCatalog()
    {
        var setup = await CreateFetchedRemoteAsync("stale");
        var service = CreateService();
        var workingDirectory = CanonicalDirectory.Create(setup.RepositoryPath);
        var catalog = await service.CaptureAsync(
            workingDirectory,
            TestContext.Current!.CancellationToken);
        var origin = catalog.Remotes.Single();
        await RunGitAsync(
            setup.RepositoryPath,
            "remote",
            "set-url",
            "--",
            "origin",
            Path.Combine(_temporaryDirectory!, "changed.git"));

        _ = await Assert.ThrowsExactlyAsync<RepositoryPreconditionException>(() => service.FetchAsync(
            workingDirectory,
            catalog,
            origin,
            FetchOptions.CreateDefault(),
            TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies one typed fetch-all transaction updates every configured remote-tracking namespace.
    /// </summary>
    [TestMethod]
    public async Task FetchAllAsync_WithTwoLocalRemotes_UpdatesEveryTrackingNamespace()
    {
        var repositoryPath = await CreateRepositoryAsync("fetch-all");
        var firstRemotePath = Path.Combine(_temporaryDirectory!, "first.git");
        var secondRemotePath = Path.Combine(_temporaryDirectory!, "second.git");
        await RunGitAsync(_temporaryDirectory!, "init", "--quiet", "--bare", "--", firstRemotePath);
        await RunGitAsync(_temporaryDirectory!, "init", "--quiet", "--bare", "--", secondRemotePath);
        await RunGitAsync(repositoryPath, "switch", "--quiet", "--create", "first-topic");
        File.AppendAllText(Path.Combine(repositoryPath, "tracked.txt"), "first\n");
        await RunGitAsync(repositoryPath, "add", "--", "tracked.txt");
        await CommitAsync(repositoryPath, "first topic");
        await RunGitAsync(repositoryPath, "push", "--quiet", firstRemotePath, "first-topic:first-topic");
        await RunGitAsync(repositoryPath, "switch", "--quiet", "main");
        await RunGitAsync(repositoryPath, "switch", "--quiet", "--create", "second-topic");
        File.AppendAllText(Path.Combine(repositoryPath, "tracked.txt"), "second\n");
        await RunGitAsync(repositoryPath, "add", "--", "tracked.txt");
        await CommitAsync(repositoryPath, "second topic");
        await RunGitAsync(repositoryPath, "push", "--quiet", secondRemotePath, "second-topic:second-topic");
        await RunGitAsync(repositoryPath, "switch", "--quiet", "main");
        await RunGitAsync(repositoryPath, "remote", "add", "first", firstRemotePath);
        await RunGitAsync(repositoryPath, "remote", "add", "second", secondRemotePath);
        var service = CreateService();
        var workingDirectory = CanonicalDirectory.Create(repositoryPath);
        var catalog = await service.CaptureAsync(
            workingDirectory,
            TestContext.Current!.CancellationToken);

        _ = await service.FetchAllAsync(
            workingDirectory,
            catalog,
            new FetchOptions(GitOptionOverride.Disabled, FetchTagMode.None),
            TestContext.Current.CancellationToken);

        Assert.HasCount(40, (await RunGitForOutputAsync(
            repositoryPath,
            "rev-parse",
            "--verify",
            "refs/remotes/first/first-topic")).Trim());
        Assert.HasCount(40, (await RunGitForOutputAsync(
            repositoryPath,
            "rev-parse",
            "--verify",
            "refs/remotes/second/second-topic")).Trim());
    }

    private RemoteService CreateService()
        => new(
            _installation!,
            _runner!,
            _environmentFactory!,
            _coordinator!,
            new CredentialPromptBroker(new TestCredentialPromptResponder()));

    private async Task<(string RepositoryPath, string RemotePath)> CreateFetchedRemoteAsync(string name)
    {
        var repositoryPath = await CreateRepositoryAsync(name);
        var remotePath = Path.Combine(_temporaryDirectory!, $"{name}.git");
        await RunGitAsync(_temporaryDirectory!, "init", "--quiet", "--bare", "--", remotePath);
        await RunGitAsync(repositoryPath, "switch", "--quiet", "--create", "feature");
        File.AppendAllText(Path.Combine(repositoryPath, "tracked.txt"), "feature\n");
        await RunGitAsync(repositoryPath, "add", "--", "tracked.txt");
        await CommitAsync(repositoryPath, "feature");
        await RunGitAsync(repositoryPath, "switch", "--quiet", "main");
        await RunGitAsync(repositoryPath, "remote", "add", "origin", remotePath);
        await RunGitAsync(repositoryPath, "push", "--quiet", "origin", "main:main", "feature:feature");
        await RunGitAsync(repositoryPath, "fetch", "--quiet", "origin");
        return (repositoryPath, remotePath);
    }

    private async Task<string> CreateRepositoryAsync(string name)
    {
        var repositoryPath = Path.Combine(_temporaryDirectory!, name);
        await RunGitAsync(
            _temporaryDirectory!,
            "init",
            "--quiet",
            "--initial-branch=main",
            "--",
            repositoryPath);
        File.WriteAllText(Path.Combine(repositoryPath, "tracked.txt"), "baseline\n");
        await RunGitAsync(repositoryPath, "add", "--", "tracked.txt");
        await CommitAsync(repositoryPath, "baseline");
        return repositoryPath;
    }

    private Task<ProcessResult> CommitAsync(string repositoryPath, string message)
        => RunGitAsync(
            repositoryPath,
            "-c",
            "user.name=GitSail Tests",
            "-c",
            "user.email=gitsail@example.invalid",
            "commit",
            "--quiet",
            "--no-gpg-sign",
            "--message",
            message);

    private async Task<string> RunGitForOutputAsync(string workingDirectory, params string[] arguments)
    {
        var result = await RunGitAsync(workingDirectory, arguments, expectSuccess: true);
        return Encoding.UTF8.GetString(result.StandardOutput.Span);
    }

    private Task<ProcessResult> RunGitAsync(string workingDirectory, params string[] arguments)
        => RunGitAsync(workingDirectory, arguments, expectSuccess: true);

    private async Task<ProcessResult> RunGitAsync(
        string workingDirectory,
        string[] arguments,
        bool expectSuccess)
    {
        var invocation = new ProcessInvocation(
            _installation!.Executable,
            [.. arguments.Select(ProcessArgument.Literal)],
            CanonicalDirectory.Create(workingDirectory),
            TestProcessEnvironment.CreateGitFactory(_temporaryDirectory!).CreateCheckoutEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(16 * 1024 * 1024, 16 * 1024 * 1024));
        var result = await _runner!.RunAsync(
            invocation,
            TestContext.Current!.CancellationToken);
        if (expectSuccess)
        {
            Assert.AreEqual(0, result.ExitCode, Encoding.UTF8.GetString(result.StandardError.Span));
        }

        return result;
    }
}
